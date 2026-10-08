import Foundation
import Network

private final class StreamReceiveState {
    let session: Session
    var transferID = UUID()
    var sequence: UInt32 = 1
    var clipboardVersion = 0
    var files: StreamIncomingFiles?
    var committed = false
    var lastPercent = -1
    init(_ session: Session) { self.session = session }
}
private final class StreamBatch {
    let plan: StreamSourcePlan
    var pending: [PeerEndpoint]
    let total: Int
    var finished = 0, succeeded = 0
    init(_ plan: StreamSourcePlan, peers: [PeerEndpoint]) { self.plan = plan; pending = peers; total = peers.count }
}
private final class StreamSendState {
    let session: Session
    let transferID = UUID()
    let batch: StreamBatch
    let host: String
    var reader: StreamSourceReader?
    var sequence: UInt32 = 1
    var acknowledged = false
    var lastPercent = -1
    init(_ session: Session, batch: StreamBatch, host: String) { self.session = session; self.batch = batch; self.host = host }
}

final class StreamEngine {
    var onStatus: (String) -> Void = { _ in }
    var onBegin: () -> Int = { 0 }
    var onReceive: (ReceivedStream, Int) throws -> Void = { _, _ in }
    var onPeerState: (String, Bool) -> Void = { _, _ in }
    let storage: StreamStorage
    private var listener: ListeningSocket?
    private var incoming: [UUID: StreamReceiveState] = [:]
    private var outgoing: [UUID: StreamSendState] = [:]
    private var peers: [PeerEndpoint] = []
    private var key = Data()
    private var generation = 0, preparing = 0
    private var replay = ReplayGuard()
    private var batch: StreamBatch?
    private(set) var active = false
    var incomingCount: Int { incoming.count }
    init(storage: StreamStorage = StreamStorage()) { self.storage = storage }
    func start(key: Data, port: UInt16 = StreamWire.port, loopbackOnly: Bool = false) throws {
        stop(); guard key.count == 32 else { throw ClipError.authentication }; self.key = key; active = true
        storage.queue.async { self.storage.cleanAbandonedTemporaryFiles() }
        let parameters = NWParameters.tcp; parameters.allowLocalEndpointReuse = true
        if loopbackOnly { parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!) }
        let listener = ListeningSocket(); self.listener = listener; let epoch = generation
        listener.onReady = { [weak self] in guard let self, self.generation == epoch else { return }; self.onStatus("文件同步已启用") }
        listener.onFailure = { [weak self] in guard let self, self.generation == epoch else { return }; self.stop(); self.onStatus("文件同步监听失败，请检查端口或网络权限") }
        listener.onConnection = { [weak self] connection in
            guard let self, self.generation == epoch, self.active, self.incoming.count < 2 else { connection.cancel(); return }
            self.receive(connection)
        }
        try listener.start(using: parameters, port: loopbackOnly ? nil : NWEndpoint.Port(rawValue: port)!)
    }
    func updatePeers(_ hosts: [String]) { updateEndpoints(hosts.map { PeerEndpoint(host: $0, port: StreamWire.port) }) }
    func updateEndpoints(_ peers: [PeerEndpoint]) { self.peers = Array(Set(peers)).sorted { ($0.host, $0.port) < ($1.host, $1.port) }.prefix(16).map { $0 } }
    func stop() {
        active = false; generation += 1; listener?.stop(); listener = nil; peers.removeAll()
        cancelTransfers(); key.resetBytes(in: 0..<key.count); key = Data()
    }
    func cancelTransfers() {
        cancelSending()
        let old = Array(incoming.values); incoming.removeAll(); old.forEach { $0.session.finish() }
    }
    func cancelSending() {
        preparing += 1
        let oldBatch = batch; batch = nil
        let old = Array(outgoing.values); outgoing.removeAll(); old.forEach { $0.session.finish() }
        if let oldBatch { dispose(oldBatch) }
    }
    private func dispose(_ batch: StreamBatch) {
        if let directory = batch.plan.ownedDirectory { storage.queue.async { try? FileManager.default.removeItem(at: directory) } }
    }
    func sendFiles(_ urls: [URL]) { prepare { try StreamSourcePlan.files(urls) } }
    func sendText(_ text: String) { let root = storage.root; prepare { try StreamSourcePlan.text(text, root: root) } }
    private func prepare(_ make: @escaping () throws -> StreamSourcePlan) {
        cancelSending()
        guard active else { onStatus("请先启用群组同步"); return }
        guard !peers.isEmpty else { onStatus("群组已保存，等待其他设备上线"); return }
        let epoch = preparing
        onStatus("正在准备传输…")
        storage.queue.async { [weak self] in
            let result = Result { try make() }
            DispatchQueue.main.async { [weak self] in
                guard let self else { if case .success(let plan) = result, let dir = plan.ownedDirectory { try? FileManager.default.removeItem(at: dir) }; return }
                guard self.active, self.preparing == epoch else {
                    if case .success(let plan) = result, let dir = plan.ownedDirectory { self.storage.queue.async { try? FileManager.default.removeItem(at: dir) } }; return
                }
                switch result {
                case .success(let plan): let batch = StreamBatch(plan, peers: self.peers); self.batch = batch; self.pump(batch)
                case .failure: self.onStatus("未发送：文字限 10 MiB，普通文件最多 32 个且合计不超过 5 GiB；请检查文件名和可读性")
                }
            }
        }
    }
    private func receive(_ connection: NWConnection) {
        let connectionID = UUID(), session = Session(connection), state = StreamReceiveState(session)
        incoming[connectionID] = state
        session.onFinish = { [weak self] in
            guard let self else { return }
            self.incoming.removeValue(forKey: connectionID)
            let files = state.files; state.files = nil
            self.storage.queue.async { files?.discard() }
        }
        session.start { [weak self] in
            guard let self else { session.finish(); return }
            session.readFrame(minimum: 61, maximum: 16_445) { [weak self] data in
                guard let self, self.active else { session.finish(); return }
                do {
                    let record = try StreamWire.open(data, key: self.key)
                    guard record.kind == 10, record.sequence == 0, self.replay.accept(record.id) else { throw ClipError.replay }
                    let offer = try StreamOffer.read(record.body)
                    state.transferID = record.id; state.clipboardVersion = self.onBegin()
                    session.allowLongTransfer()
                    self.storage.queue.async {
                        let result = Result { try StreamIncomingFiles(offer: offer, store: self.storage, id: record.id) }
                        DispatchQueue.main.async {
                            guard !session.isFinished, self.active else {
                                if case .success(let files) = result { self.storage.queue.async { files.discard() } }; return
                            }
                            switch result {
                            case .success(let files):
                                state.files = files
                                self.sendRecord(StreamRecord(id: record.id, sequence: 0, kind: 11), session: session) { self.readNext(state) }
                            case .failure: self.onStatus("接收空间不足、缓存已满或文件不可创建；请整理接收文件夹"); session.finish()
                            }
                        }
                    }
                } catch { session.finish() }
            }
        }
    }
    private func readNext(_ state: StreamReceiveState) {
        let session = state.session
        session.readFrame(minimum: 61, maximum: StreamWire.maxEnvelope) { [weak self] data in
            guard let self, self.active, let files = state.files else { session.finish(); return }
            do {
                let record = try StreamWire.open(data, key: self.key)
                guard record.id == state.transferID, record.sequence == state.sequence, record.kind == 12 || record.kind == 14 else { throw ClipError.invalid }
                if record.kind == 12 {
                    self.storage.queue.async {
                        do {
                            try files.append(record.body)
                            let percent = files.offer.total == 0 ? 100 : Int(files.received * 100 / files.offer.total)
                            DispatchQueue.main.async {
                                guard !session.isFinished else { return }
                                if percent != state.lastPercent { state.lastPercent = percent; self.onStatus("正在接收\(files.offer.type == "text" ? "文字" : "文件") · \(percent)%") }
                                state.sequence += 1
                                self.sendRecord(StreamRecord(id: record.id, sequence: record.sequence, kind: 13), session: session) { self.readNext(state) }
                            }
                        } catch { DispatchQueue.main.async { self.onStatus("接收未完成：文件数据无效或磁盘写入失败"); session.finish() } }
                    }
                } else {
                    self.storage.queue.async {
                        do {
                            let received = try files.finish(record.body)
                            DispatchQueue.main.async {
                                guard !session.isFinished, self.active else { return }
                                do {
                                    try self.onReceive(received, state.clipboardVersion)
                                    state.committed = true
                                    self.storage.queue.async {
                                        files.keep()
                                        DispatchQueue.main.async {
                                            guard !session.isFinished else { return }
                                            self.sendRecord(StreamRecord(id: record.id, sequence: record.sequence, kind: 15), session: session) { session.finish() }
                                            self.onStatus("已接收\(files.offer.type == "text" ? "文字" : "文件")，可以粘贴")
                                        }
                                    }
                                } catch { self.onStatus("接收已取消：剪贴板已有新内容或无法写入"); session.finish() }
                            }
                        } catch { DispatchQueue.main.async { self.onStatus("接收失败：完整性校验未通过"); session.finish() } }
                    }
                }
            } catch { session.finish() }
        }
    }
    private func sendRecord(_ record: StreamRecord, session: Session, done: @escaping () -> Void) {
        guard !session.isFinished else { return }
        do { session.send(try StreamWire.seal(record, key: key), done: done) } catch { session.finish() }
    }
    private func pump(_ batch: StreamBatch) {
        guard active, self.batch === batch else { return }
        while outgoing.count < 2, !batch.pending.isEmpty { sendOne(batch, to: batch.pending.removeFirst()) }
    }
    private func sendOne(_ batch: StreamBatch, to peer: PeerEndpoint) {
        let id = UUID(), session = Session(NWConnection(host: NWEndpoint.Host(peer.host), port: NWEndpoint.Port(rawValue: peer.port)!, using: .tcp))
        let state = StreamSendState(session, batch: batch, host: peer.host); outgoing[id] = state
        session.onFinish = { [weak self] in
            guard let self else { return }
            self.outgoing.removeValue(forKey: id)
            let reader = state.reader; state.reader = nil
            self.storage.queue.async { withExtendedLifetime(reader) {} }
            guard self.active, self.batch === batch else { return }
            self.onPeerState(peer.host, state.acknowledged)
            batch.finished += 1; if state.acknowledged { batch.succeeded += 1 }
            if batch.finished == batch.total {
                self.batch = nil; self.dispose(batch)
                let what = batch.plan.offer.type == "text" ? "文字" : "文件"
                self.onStatus(batch.succeeded == batch.total ? "\(what)发送成功（\(batch.total) 台）" : "\(what)已送达 \(batch.succeeded)/\(batch.total) 台；离线、取消或文件变化可导致未送达")
            } else { self.pump(batch) }
        }
        session.start { [weak self] in
            guard let self else { session.finish(); return }
            self.storage.queue.async {
                let result = Result { try StreamSourceReader(batch.plan) }
                DispatchQueue.main.async {
                    guard !session.isFinished else { return }
                    switch result {
                    case .success(let reader):
                        state.reader = reader
                        do {
                            self.sendRecord(StreamRecord(id: state.transferID, sequence: 0, kind: 10, body: try batch.plan.offer.bytes()), session: session) {
                                self.readAck(state, kind: 11, sequence: 0) { session.allowLongTransfer(); self.sendNext(state) }
                            }
                        } catch { session.finish() }
                    case .failure: session.finish()
                    }
                }
            }
        }
    }
    private func readAck(_ state: StreamSendState, kind: UInt8, sequence: UInt32, done: @escaping () -> Void) {
        state.session.readFrame(minimum: 61, maximum: 61) { [weak self] data in
            guard let self, let ack = try? StreamWire.open(data, key: self.key), ack.id == state.transferID,
                  ack.kind == kind, ack.sequence == sequence, ack.body.isEmpty else { state.session.finish(); return }
            done()
        }
    }
    private func sendNext(_ state: StreamSendState) {
        guard active, !state.session.isFinished, let reader = state.reader else { return }
        storage.queue.async {
            do {
                let chunk = try reader.next(), sent = reader.sent
                let body = try chunk ?? reader.digests()
                DispatchQueue.main.async {
                    guard !state.session.isFinished, self.active else { return }
                    let sequence = state.sequence
                    if chunk != nil {
                        self.sendRecord(StreamRecord(id: state.transferID, sequence: sequence, kind: 12, body: body), session: state.session) {
                            self.readAck(state, kind: 13, sequence: sequence) {
                                let total = state.batch.plan.offer.total
                                let percent = total == 0 ? 100 : Int(sent * 100 / total)
                                if percent != state.lastPercent { state.lastPercent = percent; self.onStatus("正在发送\(state.batch.plan.offer.type == "text" ? "文字" : "文件") · \(percent)%") }
                                state.sequence += 1; self.sendNext(state)
                            }
                        }
                    } else {
                        self.sendRecord(StreamRecord(id: state.transferID, sequence: sequence, kind: 14, body: body), session: state.session) {
                            self.readAck(state, kind: 15, sequence: sequence) { state.acknowledged = true; state.session.finish() }
                        }
                    }
                }
            } catch { DispatchQueue.main.async { state.session.finish() } }
        }
    }
}
