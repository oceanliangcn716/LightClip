import Foundation
import Network

// Network.framework releases a cancelled listener asynchronously. A new listener
// on the same port can briefly see EADDRINUSE during pause/resume or wake-up.
final class ListeningSocket {
    var onReady: () -> Void = {}
    var onFailure: () -> Void = {}
    var onConnection: (NWConnection) -> Void = { $0.cancel() }
    private var listener: NWListener?
    private var generation = 0
    func start(using parameters: NWParameters, port: NWEndpoint.Port?) throws {
        stop()
        try listen(using: parameters, port: port, epoch: generation, attempt: 0)
    }
    private func listen(using parameters: NWParameters, port: NWEndpoint.Port?, epoch: Int, attempt: Int) throws {
        let next = try NWListener(using: parameters, on: port ?? .any)
        listener = next
        next.newConnectionHandler = { [weak self] connection in
            guard let self, self.generation == epoch else { connection.cancel(); return }
            self.onConnection(connection)
        }
        next.stateUpdateHandler = { [weak self] state in
            guard let self, self.generation == epoch else { return }
            switch state {
            case .ready: self.onReady()
            case .failed(let error):
                self.listener?.cancel(); self.listener = nil
                if case .posix(.EADDRINUSE) = error, attempt < 8 {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
                        guard let self, self.generation == epoch else { return }
                        do { try self.listen(using: parameters, port: port, epoch: epoch, attempt: attempt + 1) }
                        catch { self.onFailure() }
                    }
                } else { self.onFailure() }
            default: break
            }
        }
        next.start(queue: .main)
    }
    func stop() {
        generation += 1
        listener?.stateUpdateHandler = nil; listener?.newConnectionHandler = nil
        listener?.cancel(); listener = nil
    }
    deinit { listener?.cancel() }
}

// Each connection owns one request and one authenticated acknowledgement.
// An absolute deadline bounds partial/slow frames and all retained buffers.
final class Session {
    let connection: NWConnection
    private var finished = false
    private var deadline: DispatchWorkItem?
    private var idleTimer: DispatchSourceTimer?
    private var lastProgress = Date()
    var isFinished: Bool { finished }
    var onFinish: (() -> Void)?
    init(_ connection: NWConnection) { self.connection = connection }
    func start(ready: @escaping () -> Void) {
        connection.stateUpdateHandler = { [weak self] state in
            guard let self, !self.finished else { return }
            switch state {
            case .ready: ready()
            case .failed, .cancelled: self.finish()
            default: break
            }
        }
        let timer = DispatchWorkItem { [weak self] in self?.finish() }
        deadline = timer
        DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: timer)
        connection.start(queue: .main)
    }
    func finish() {
        guard !finished else { return }
        finished = true
        deadline?.cancel(); deadline = nil
        idleTimer?.cancel(); idleTimer = nil
        connection.stateUpdateHandler = nil
        connection.cancel()
        let callback = onFinish; onFinish = nil; callback?()
    }
    // Only call after authenticating the stream's offer/ready record.
    func allowLongTransfer() {
        guard !finished else { return }
        deadline?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.finish() }; deadline = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 24 * 60 * 60, execute: work)
        lastProgress = Date()
        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(deadline: .now() + 5, repeating: 5)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            if Date().timeIntervalSince(self.lastProgress) >= 30 { self.finish() }
        }
        idleTimer?.cancel(); idleTimer = timer; timer.resume()
    }
    func readExact(_ count: Int, buffer: Data = Data(), done: @escaping (Data) -> Void) {
        guard !finished, count >= 0 else { return }
        if buffer.count == count { done(buffer); return }
        connection.receive(minimumIncompleteLength: 1, maximumLength: count - buffer.count) { [weak self] bytes, _, complete, error in
            guard let self, !self.finished else { return }
            var result = buffer
            if let bytes { result.append(bytes); if !bytes.isEmpty { self.lastProgress = Date() } }
            if result.count == count { done(result) }
            else if error != nil || complete { self.finish() }
            else { self.readExact(count, buffer: result, done: done) }
        }
    }
    func readFrame(minimum: Int = 57, maximum: Int = Wire.maxEnvelope, _ done: @escaping (Data) -> Void) {
        readExact(4) { [weak self] header in
            guard let self else { return }
            let n = Int(Wire.get64(header))
            guard n >= minimum, n <= maximum else { self.finish(); return }
            self.readExact(n, done: done)
        }
    }
    func send(_ bytes: Data, done: @escaping () -> Void) {
        guard !finished else { return }
        connection.send(content: bytes, completion: .contentProcessed { [weak self] error in
            guard let self, !self.finished else { return }
            if error == nil { self.lastProgress = Date(); done() } else { self.finish() }
        })
    }
}

struct PeerEndpoint: Hashable {
    let host: String
    let port: UInt16
}

final class SyncEngine {
    var onStatus: (String) -> Void = { _ in }
    var onReceive: (Packet) throws -> Void = { _ in }
    var onPeerState: (String, Bool) -> Void = { _, _ in }
    private var listener: ListeningSocket?
    private var sessions: [UUID: Session] = [:]
    private var outgoing: [UUID: Session] = [:]
    private var probes: [String: Session] = [:]
    private var key = Data()
    private var peers: [PeerEndpoint] = []
    private var peerPort = Wire.port
    private var generation = 0
    private var replay = ReplayGuard()
    private(set) var active = false
    private final class Batch {
        let packet: Packet
        let total: Int
        var pending: [PeerEndpoint]
        var finished = 0
        var succeeded = 0
        init(_ packet: Packet, hosts: [PeerEndpoint]) { self.packet = packet; pending = hosts; total = hosts.count }
    }
    private var batch: Batch?
    func start(host: String, key: Data, loopbackOnly: Bool = false, listenPort: UInt16 = Wire.port, peerPort: UInt16 = Wire.port, bindHost: String = "127.0.0.1") throws {
        stop()
        self.key = key; self.peerPort = peerPort
        updatePeers(host.isEmpty ? [] : [host])
        let parameters = NWParameters.tcp
        parameters.allowLocalEndpointReuse = true
        if loopbackOnly { parameters.requiredLocalEndpoint = .hostPort(host: NWEndpoint.Host(bindHost), port: NWEndpoint.Port(rawValue: listenPort)!) }
        let listener = ListeningSocket()
        self.listener = listener; active = true
        let epoch = generation
        listener.onReady = { [weak self] in
            guard let self, self.generation == epoch else { return }
            self.onStatus("已启用，等待群组设备上线")
        }
        listener.onFailure = { [weak self] in
            guard let self, self.generation == epoch else { return }
            self.stop(); self.onStatus("监听失败：端口被占用或网络权限受限")
        }
        listener.onConnection = { [weak self] c in
            guard let self, self.active, self.generation == epoch, self.sessions.count < 4 else { c.cancel(); return }
            self.receive(c, epoch: epoch)
        }
        try listener.start(using: parameters, port: loopbackOnly ? nil : NWEndpoint.Port(rawValue: listenPort)!)
    }
    func updatePeers(_ hosts: [String]) { updateEndpoints(hosts.map { PeerEndpoint(host: $0, port: peerPort) }) }
    func updateEndpoints(_ endpoints: [PeerEndpoint]) {
        peers = Array(Set(endpoints)).sorted { ($0.host, $0.port) < ($1.host, $1.port) }.prefix(16).map { $0 }
    }
    func stop() {
        active = false; generation += 1
        listener?.stop(); listener = nil; batch = nil
        let existing = Array(sessions.values) + Array(outgoing.values) + Array(probes.values)
        sessions.removeAll(); outgoing.removeAll(); probes.removeAll(); peers.removeAll()
        existing.forEach { $0.finish() }
        key.resetBytes(in: 0..<key.count); key = Data()
        // Preserve replay records across pause/resume and address changes.
    }
    private func receive(_ connection: NWConnection, epoch: Int) {
        let id = UUID(), session = Session(connection)
        sessions[id] = session
        session.onFinish = { [weak self] in self?.sessions.removeValue(forKey: id) }
        session.start { [weak self, weak session] in
            guard let self, let session else { return }
            session.readFrame { [weak self, weak session] envelope in
                guard let self, let session, self.active, self.generation == epoch else { session?.finish(); return }
                do {
                    let packet = try Wire.open(envelope, key: self.key)
                    guard packet.kind != 3, self.replay.accept(packet.id) else { throw ClipError.replay }
                    try self.onReceive(packet)
                    if case let .hostPort(host, _) = connection.endpoint { self.onPeerState(String(describing: host), true) }
                    let bytes = Wire.frame(try Wire.seal(Packet(id: packet.id, kind: 3, body: Data()), key: self.key))
                    session.send(bytes) { session.finish() }
                    self.onStatus(packet.kind == 0 ? "群组设备已连接" : "已接收\(packet.kind == 1 ? "文字" : "图片")")
                } catch { session.finish(); self.onStatus("接收未完成：密钥、数据或剪贴板不可用") }
            }
        }
    }
    func probe(_ host: String) {
        guard active, probes[host] == nil, probes.count < 4 else { return }
        let epoch = generation, packet = Packet(kind: 0, body: Data())
        let session = Session(NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: peerPort)!, using: .tcp))
        probes[host] = session
        var verified = false
        session.onFinish = { [weak self, weak session] in
            guard let self, self.generation == epoch, self.probes[host] === session else { return }
            self.probes.removeValue(forKey: host); self.onPeerState(host, verified)
        }
        do {
            let frame = Wire.frame(try Wire.seal(packet, key: key))
            session.start { [weak self, weak session] in
                guard let self, let session else { return }
                session.send(frame) { [weak self, weak session] in
                    guard let self, let session else { return }
                    session.readFrame { data in
                        if let ack = try? Wire.open(data, key: self.key), ack.kind == 3, ack.id == packet.id { verified = true }
                        session.finish()
                    }
                }
            }
        } catch { session.finish() }
    }
    func send(_ packet: Packet) {
        guard active else { onStatus("请先启用同步"); return }
        // A fresh local copy cancels the entire older fanout, including pending peers.
        cancelSending()
        guard !peers.isEmpty else { onStatus("群组已保存，等待其他设备上线"); return }
        do { try Wire.validate(packet) } catch { onStatus("内容超出限制或数据无效"); return }
        let next = Batch(packet, hosts: peers); batch = next
        onStatus(packet.kind == 0 ? "正在测试连接…" : "正在发送…")
        pump(next)
    }
    func cancelSending() {
        batch = nil
        let old = Array(outgoing.values); outgoing.removeAll(); old.forEach { $0.finish() }
    }
    private func pump(_ current: Batch) {
        guard active, batch === current else { return }
        // At most four encrypted image buffers, even for a larger group.
        while outgoing.count < 4, !current.pending.isEmpty { sendOne(current, destination: current.pending.removeFirst()) }
    }
    private func sendOne(_ current: Batch, destination: PeerEndpoint) {
        let host = destination.host
        let epoch = generation, id = UUID()
        let session = Session(NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: destination.port)!, using: .tcp))
        outgoing[id] = session
        var acknowledged = false
        session.onFinish = { [weak self] in
            guard let self, self.generation == epoch, self.batch === current else { return }
            self.outgoing.removeValue(forKey: id)
            self.onPeerState(host, acknowledged)
            current.finished += 1
            if acknowledged { current.succeeded += 1 }
            if current.finished == current.total {
                self.batch = nil
                let what = current.packet.kind == 0 ? "群组连接" : (current.packet.kind == 1 ? "文字" : "图片")
                if current.succeeded == current.total {
                    self.onStatus(current.packet.kind == 0 ? "连接测试成功" : (current.total == 1 ? "\(what)发送成功" : "\(what)发送成功（\(current.total) 台）"))
                } else {
                    self.onStatus(current.succeeded == 0 ? "未送达：设备可能离线或暂时无法连接" : "\(what)已送达 \(current.succeeded)/\(current.total) 台")
                }
            } else { self.pump(current) }
        }
        do {
            let frame = Wire.frame(try Wire.seal(current.packet, key: key))
            session.start { [weak self, weak session] in
                guard let self, let session else { return }
                session.send(frame) { [weak self, weak session] in
                    guard let self, let session else { return }
                    session.readFrame { data in
                        if let ack = try? Wire.open(data, key: self.key), ack.kind == 3, ack.id == current.packet.id { acknowledged = true }
                        session.finish()
                    }
                }
            }
        } catch { session.finish() }
    }
}
