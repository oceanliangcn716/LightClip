import AppKit
import CryptoKit

enum StreamTest {
    static func require(_ value: @autoclosure () throws -> Bool, _ label: String) throws {
        let result = try value(); try GroupTest.require(result, "stream: " + label)
    }
    static func temp() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("LightClip-stream-test-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); return root
    }
    static func fixture(_ root: URL) throws -> [URL] {
        let file = root.appendingPathComponent("payload.bin"), empty = root.appendingPathComponent("empty.txt")
        try Data((0..<2_097_169).map { UInt8($0 % 251) }).write(to: file)
        try Data().write(to: empty); return [file, empty]
    }
    static func validateFixture(_ result: ReceivedStream) throws {
        try require(result.offer.type == "files" && result.offer.items.count == 2, "interop manifest")
        let bytes = try Data(contentsOf: result.urls[0])
        try require(bytes.count == 2_097_169 && bytes.enumerated().allSatisfy { $0.element == UInt8($0.offset % 251) }, "interop file bytes")
        try require(try FileIdentity.at(result.urls[1]).size == 0, "interop empty file")
    }
    static func unit() throws {
        let offer = StreamOffer(type: "files", items: [StreamItem(name: "largest.bin", size: StreamWire.maxFiles)], total: StreamWire.maxFiles)
        try offer.validate()
        try SelfTest.rejected { try StreamOffer(type: "files", items: [StreamItem(name: "large.bin", size: StreamWire.maxFiles + 1)], total: StreamWire.maxFiles + 1).validate() }
        try SelfTest.rejected { try StreamOffer(type: "files", items: [StreamItem(name: "a", size: 1), StreamItem(name: "A", size: 1)], total: 2).validate() }
        try SelfTest.rejected { try StreamOffer(type: "text", items: [StreamItem(name: "clipboard.txt", size: UInt64(StreamWire.maxText + 1))], total: UInt64(StreamWire.maxText + 1)).validate() }
        for name in ["../bad", "..", "folder/file", "C:\\file", "bad.", "CON.txt", "lpt9", "bad\n", "a?b"] { try require(!StreamWire.validName(name), "unsafe name rejected") }
        let record = StreamRecord(id: GroupTest.clientID, sequence: 42, kind: 12, body: Data(repeating: 7, count: StreamWire.chunkBytes + 12))
        let frame = try StreamWire.seal(record, key: SelfTest.testKey), decoded = try StreamWire.open(Data(frame.dropFirst(4)), key: SelfTest.testKey)
        try require(frame.count == StreamWire.maxEnvelope + 4 && decoded.id == record.id && decoded.sequence == 42 && decoded.body == record.body, "max record roundtrip")
        try SelfTest.rejected { _ = try StreamWire.open(Data(frame.dropFirst(4)), key: Data(repeating: 4, count: 32)) }
        var bad = frame; bad[30] ^= 1
        try SelfTest.rejected { _ = try StreamWire.open(Data(bad.dropFirst(4)), key: SelfTest.testKey) }
        try SelfTest.rejected { _ = try StreamWire.open(Data(try StreamWire.seal(StreamRecord(id: UUID(), sequence: 0, kind: 10, timestamp: 0), key: SelfTest.testKey).dropFirst(4)), key: SelfTest.testKey) }
        var utf = UTF8StreamValidator(); try utf.consume(Data([0xf0, 0x9f])); try require(!utf.complete, "partial unicode"); try utf.consume(Data([0x96, 0xa5])); try require(utf.complete, "unicode cross chunk")
        for bytes: [UInt8] in [[0xc0,0x80], [0xed,0xa0,0x80], [0xf4,0x90,0x80,0x80]] { try SelfTest.rejected { var v = UTF8StreamValidator(); try v.consume(Data(bytes)) } }
        let root = try temp(); defer { try? FileManager.default.removeItem(at: root) }
        let urls = try fixture(root), plan = try StreamSourcePlan.files(urls)
        let reader = try StreamSourceReader(plan), store = StreamStorage(root: root.appendingPathComponent("cache"))
        let incoming = try StreamIncomingFiles(offer: plan.offer, store: store, id: UUID())
        try SelfTest.rejected { try incoming.append(StreamWire.put32(0) + Wire.put64(1) + Data([1])) }
        while let body = try reader.next() { try incoming.append(body) }
        try SelfTest.rejected { _ = try incoming.finish(Data(repeating: 0, count: 64)) }
        incoming.discard()
        let second = try StreamIncomingFiles(offer: plan.offer, store: store, id: UUID()), again = try StreamSourceReader(plan)
        while let body = try again.next() { try second.append(body) }
        let result = try second.finish(again.digests()); try validateFixture(result); second.keep()
        let link = root.appendingPathComponent("link.bin"); try FileManager.default.createSymbolicLink(at: link, withDestinationURL: urls[0])
        try SelfTest.rejected { _ = try StreamSourcePlan.files([link]) }
        try SelfTest.rejected { _ = try StreamSourcePlan.files([root]) }
        try Data([1]).write(to: urls[0]); try SelfTest.rejected { _ = try StreamSourceReader(plan) }
        let abandoned = store.root.appendingPathComponent(UUID().uuidString + ".partial", isDirectory: true)
        try FileManager.default.createDirectory(at: abandoned, withIntermediateDirectories: false)
        store.cleanAbandonedTemporaryFiles()
        try require(!FileManager.default.fileExists(atPath: abandoned.path) && FileManager.default.fileExists(atPath: result.urls[0].path), "startup cleans only abandoned temporary directories")
        print("PASS: stream record crypto, 10MiB/5GiB bounds, path/duplicate/symlink rejection, UTF8 cross-chunk validation, file mutation, offsets and SHA256")
    }
    static func run() throws {
        try unit()
        let root = try temp(), store1 = StreamStorage(root: root.appendingPathComponent("cache1")), store2 = StreamStorage(root: root.appendingPathComponent("cache2"))
        let sender = StreamEngine(storage: StreamStorage(root: root.appendingPathComponent("outgoing"))), first = StreamEngine(storage: store1), second = StreamEngine(storage: store2)
        let pb1 = NSPasteboard.withUniqueName(), pb2 = NSPasteboard.withUniqueName(), clip1 = ClipBoard(board: pb1), clip2 = ClipBoard(board: pb2)
        defer { sender.stop(); first.stop(); second.stop(); clip1.stop(); clip2.stop(); store1.queue.sync {}; store2.queue.sync {}; sender.storage.queue.sync {}; pb1.releaseGlobally(); pb2.releaseGlobally(); try? FileManager.default.removeItem(at: root) }
        let urls = try fixture(root)
        var ready = 0, count1 = 0, count2 = 0, echoes = 0, status = "", incomingStatus = ""
        first.onBegin = { clip1.changeVersion }; second.onBegin = { clip2.changeVersion }
        first.onReceive = { result, version in try clip1.apply(result, expectedVersion: version); count1 += 1 }
        second.onReceive = { result, version in try clip2.apply(result, expectedVersion: version); count2 += 1 }
        first.onStatus = { incomingStatus = $0; if $0 == "文件同步已启用" { ready += 1 } }
        second.onStatus = { if $0 == "文件同步已启用" { ready += 1 } }
        sender.onStatus = { status = $0; if $0 == "文件同步已启用" { ready += 1 } }
        clip1.onLocalChange = { echoes += 1 }; clip2.onLocalChange = { echoes += 1 }; clip1.start(); clip2.start()
        try first.start(key: SelfTest.testKey, port: 49401, loopbackOnly: true)
        try second.start(key: SelfTest.testKey, port: 49402, loopbackOnly: true)
        try sender.start(key: SelfTest.testKey, port: 49400, loopbackOnly: true)
        try GroupTest.wait("stream listeners ready", until: { ready == 3 })
        sender.updateEndpoints([PeerEndpoint(host: "127.0.0.1", port: 49401), PeerEndpoint(host: "127.0.0.1", port: 49402)])
        sender.sendFiles(urls)
        try GroupTest.wait("stream multi-peer file delivery", seconds: 20, until: { count1 == 1 && count2 == 1 && status.contains("发送成功") })
        for pb in [pb1, pb2] {
            let received = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]
            try require(received?.count == 2 && (try? Data(contentsOf: received![0])) == Data(contentsOf: urls[0]), "file pasteboard URLs and original bytes")
        }
        let png = root.appendingPathComponent("image.png"); try SelfTest.fixturePNG.write(to: png)
        sender.sendFiles([png]); try GroupTest.wait("stream image file delivery", until: { count1 == 2 && count2 == 2 && status.contains("发送成功") })
        try require(pb1.data(forType: .png) == SelfTest.fixturePNG && NSImage(pasteboard: pb1) != nil, "image plus file paste formats")
        let text = String(repeating: "a", count: StreamWire.maxText - 6) + "中文"
        var textDirectory: URL?
        first.onReceive = { result, version in try clip1.apply(result, expectedVersion: version); count1 += 1; if result.offer.type == "text" { textDirectory = result.directory } }
        sender.sendText(text)
        try GroupTest.wait("10MiB text delivered", seconds: 30, until: { count1 == 3 && count2 == 3 && status.contains("发送成功") })
        try require(pb1.string(forType: .string) == text && pb2.string(forType: .string) == text, "10MiB UTF8 clipboard exact")
        try require(textDirectory != nil && !FileManager.default.fileExists(atPath: textDirectory!.path), "text staging deleted after clipboard commit")
        RunLoop.main.run(until: Date(timeIntervalSinceNow: 0.85)); try require(echoes == 0, "stream receive never echoed")
        // Production capture routes ordinary files and >1MiB text to stream callbacks.
        let capturePB = NSPasteboard.withUniqueName(), capture = ClipBoard(board: capturePB)
        defer { capturePB.releaseGlobally() }
        var fileCaptures = 0, textCaptures = 0
        capture.onFiles = { fileCaptures += $0.count }; capture.onLargeText = { if $0 == text { textCaptures += 1 } }
        capturePB.clearContents(); capturePB.writeObjects(urls.map { $0 as NSURL }); capture.capture(); try require(fileCaptures == 2, "generic file capture")
        capturePB.clearContents(); capturePB.setString(text, forType: .string); capture.capture(); try require(textCaptures == 1, "large text capture")
        capturePB.clearContents(); capturePB.setString(text + "a", forType: .string); capture.capture(); try require(textCaptures == 1, "oversize text skipped")
        second.stop(); sender.sendFiles(urls)
        try GroupTest.wait("offline peer partial stream delivery", seconds: 15, until: { count1 == 4 && status.contains("1/2") })
        sender.updateEndpoints([PeerEndpoint(host: "127.0.0.1", port: 49401)])
        var changed = false
        first.onStatus = { value in
            incomingStatus = value
            if value.hasPrefix("正在接收"), !changed { changed = true; pb1.clearContents(); pb1.setString("new local clipboard", forType: .string) }
        }
        sender.sendFiles(urls)
        try GroupTest.wait("new clipboard rejects stale stream", seconds: 15, until: { status.contains("0/1") })
        try require(count1 == 4 && pb1.string(forType: .string) == "new local clipboard" && incomingStatus.contains("已取消"), "local clipboard preserved")
        var cancelled = false
        first.onStatus = { incomingStatus = $0 }
        sender.onStatus = { value in
            status = value
            if value.hasPrefix("正在发送文件"), !cancelled { cancelled = true; sender.cancelTransfers() }
        }
        sender.sendFiles(urls)
        try GroupTest.wait("cancel issued", until: { cancelled })
        try GroupTest.wait("cancel receiver cleanup", until: { first.incomingCount == 0 })
        store1.queue.sync {}
        let remaining = try FileManager.default.contentsOfDirectory(at: store1.root, includingPropertiesForKeys: nil)
        try require(!remaining.contains { $0.pathExtension == "partial" } && count1 == 4, "cancel partial cleanup and no commit")
        print("PASS: stream two-peer ordinary/empty/image files, exact 10MiB text, file URLs+PNG/TIFF, capture routing, echo suppression, offline partial, stale clipboard and cancellation cleanup")
    }
    static func big() throws {
        let root = try temp(), sender = StreamEngine(storage: StreamStorage(root: FileManager.default.temporaryDirectory.appendingPathComponent("LightClip-unused-" + UUID().uuidString))), receiver = StreamEngine(storage: StreamStorage(root: root.appendingPathComponent("cache")))
        defer { sender.stop(); receiver.stop(); sender.storage.queue.sync {}; receiver.storage.queue.sync {}; try? FileManager.default.removeItem(at: root) }
        let url = root.appendingPathComponent("five-gib.bin")
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let handle = try FileHandle(forWritingTo: url); try handle.truncate(atOffset: StreamWire.maxFiles); try handle.close()
        var ready = 0, received = false, success = false, lastDecile = -1
        receiver.onReceive = { result, _ in
            let size = try FileIdentity.at(result.urls[0]).size
            try require(size == StreamWire.maxFiles, "full 5GiB destination size"); received = true
        }
        receiver.onStatus = { if $0 == "文件同步已启用" { ready += 1 } }
        sender.onStatus = { value in
            if value == "文件同步已启用" { ready += 1 }
            if value.contains("发送成功") { success = true }
            if value.hasPrefix("正在发送文件"), let percentText = value.components(separatedBy: " · ").last?.dropLast(), let percent = Int(percentText), percent / 10 != lastDecile {
                lastDecile = percent / 10; print("BIG_STREAM_PROGRESS \(percent)%"); fflush(stdout)
            }
        }
        try receiver.start(key: SelfTest.testKey, port: 49411, loopbackOnly: true)
        try sender.start(key: SelfTest.testKey, port: 49410, loopbackOnly: true)
        try GroupTest.wait("big listeners", until: { ready == 2 })
        sender.updateEndpoints([PeerEndpoint(host: "127.0.0.1", port: 49411)])
        let begin = Date(); sender.sendFiles([url])
        try GroupTest.wait("actual 5GiB encrypted stream", seconds: 600, until: { received && success })
        print("PASS: actual 5368709120-byte file streamed, SHA256 verified, committed and acknowledged; seconds=\(String(format: "%.2f", Date().timeIntervalSince(begin)))")
    }
    static func server() {
        do {
            let root = try temp(), engine = StreamEngine(storage: StreamStorage(root: root)), board = NSPasteboard.withUniqueName(), clip = ClipBoard(board: board)
            var received = false
            engine.onBegin = { clip.changeVersion }
            engine.onReceive = { result, version in try validateFixture(result); try clip.apply(result, expectedVersion: version); received = true }
            engine.onStatus = { value in
                if value == "文件同步已启用" { print("STREAM_READY"); fflush(stdout) }
                if value.hasPrefix("已接收文件"), received {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { engine.stop(); engine.storage.queue.sync {}; board.releaseGlobally(); try? FileManager.default.removeItem(at: root); print("PASS: C# to Swift stream files, SHA256, file clipboard and final ACK"); exit(0) }
                }
            }
            try engine.start(key: SelfTest.testKey, port: 49399, loopbackOnly: true)
            DispatchQueue.main.asyncAfter(deadline: .now() + 30) { print("STREAM_SERVER_TIMEOUT"); exit(1) }
            RunLoop.main.run()
        } catch { print("STREAM_SERVER_FAILED"); exit(1) }
    }
    static func client() {
        do {
            let root = try temp(), urls = try fixture(root), engine = StreamEngine(storage: StreamStorage(root: root.appendingPathComponent("cache")))
            engine.onStatus = { value in
                if value == "文件同步已启用" { engine.sendFiles(urls) }
                if value.contains("发送成功") { engine.stop(); engine.storage.queue.sync {}; try? FileManager.default.removeItem(at: root); print("PASS: Swift to C# stream files and final ACK"); exit(0) }
                if value.contains("0/1") { print("STREAM_CLIENT_FAILED"); exit(1) }
            }
            try engine.start(key: SelfTest.testKey, port: 49403, loopbackOnly: true)
            engine.updateEndpoints([PeerEndpoint(host: "127.0.0.1", port: 49399)])
            DispatchQueue.main.asyncAfter(deadline: .now() + 30) { print("STREAM_CLIENT_TIMEOUT"); exit(1) }
            RunLoop.main.run()
        } catch { print("STREAM_CLIENT_FAILED"); exit(1) }
    }
}
