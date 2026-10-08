import AppKit
import CryptoKit

enum SelfTest {
    static let fixturePNG = Data(base64Encoded: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")!
    // Public synthetic test key; never used for application pairing.
    static let testKey = Data(0..<32)
    static var interopPort: UInt16 { UInt16(ProcessInfo.processInfo.environment["LIGHTCLIP_TEST_PORT"] ?? "") ?? Wire.port }
    static func require(_ condition: @autoclosure () -> Bool) throws { if !condition() { throw ClipError.invalid } }
    static func rejected(_ action: () throws -> Void) throws {
        var threw = false
        do { try action() } catch { threw = true }
        try require(threw)
    }
    static func run() throws {
        let original = Packet(kind: 1, body: Data("轻剪 test 🖥️\r\nsecond line".utf8))
        let encoded = try Wire.seal(original, key: testKey)
        let decoded = try Wire.open(encoded, key: testKey)
        try require(decoded.id == original.id && decoded.body == original.body && decoded.kind == 1)
        try rejected { _ = try Wire.open(encoded, key: Data(repeating: 99, count: 32)) }
        var changed = encoded; changed[15] ^= 1
        try rejected { _ = try Wire.open(changed, key: testKey) }
        try rejected { _ = try Wire.open(Data(encoded.prefix(30)), key: testKey) }
        try rejected { try Wire.validate(Packet(timestamp: 0, kind: 0, body: Data())) }
        try rejected { try Wire.validate(Packet(kind: 1, body: Data([0xff]))) }
        try rejected { try Wire.validate(Packet(kind: 1, body: Data(repeating: 65, count: Wire.maxText + 1))) }
        try rejected { try Wire.validate(Packet(kind: 2, body: Data([0,1,2]))) }
        try rejected { try Wire.validate(Packet(kind: 3, body: Data([1]))) }
        let replay = ReplayGuard(), id = UUID()
        try require(replay.accept(id)); try require(!replay.accept(id))
        let pasteboard = NSPasteboard.withUniqueName()
        defer { pasteboard.releaseGlobally() }
        let clip = ClipBoard(board: pasteboard)
        var captures = 0
        clip.onChange = { p in if p.body == original.body { captures += 1 } }
        pasteboard.clearContents()
        pasteboard.setString(String(data: original.body, encoding: .utf8)!, forType: .string)
        clip.capture(); try require(captures == 1)
        try clip.apply(Packet(kind: 2, body: fixturePNG))
        try require(pasteboard.data(forType: .png) == fixturePNG)
        try require(pasteboard.types?.contains(.tiff) == true)
        try require(NSImage(pasteboard: pasteboard) != nil)
        try require(pasteboard.data(forType: .tiff).flatMap { NSBitmapImageRep(data: $0) } != nil)
        // Exercise an actual TIFF-only consumer and preserve transparent colored pixels.
        let pixels = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 32, pixelsHigh: 24, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        for y in 0..<24 { for x in 0..<32 { pixels.setColor(NSColor(deviceRed: CGFloat(x) / 31, green: CGFloat(y) / 23, blue: 0.3, alpha: 0.5), atX: x, y: y) } }
        let coloredPNG = pixels.representation(using: .png, properties: [:])!
        try clip.apply(Packet(kind: 2, body: coloredPNG))
        let tiff = pasteboard.data(forType: .tiff)!
        let tiffImage = NSBitmapImageRep(data: tiff)!, pngImage = NSBitmapImageRep(data: coloredPNG)!
        try require(tiffImage.pixelsWide == 32 && tiffImage.pixelsHigh == 24 && tiffImage.hasAlpha)
        let before = pngImage.colorAt(x: 17, y: 13)!.usingColorSpace(.deviceRGB)!
        let after = tiffImage.colorAt(x: 17, y: 13)!.usingColorSpace(.deviceRGB)!
        try require(abs(before.redComponent - after.redComponent) < 0.02 && abs(before.greenComponent - after.greenComponent) < 0.02 && abs(before.alphaComponent - after.alphaComponent) < 0.02)
        let mixed = NSPasteboardItem()
        mixed.setData(coloredPNG, forType: .png); mixed.setString("image caption", forType: .string)
        pasteboard.clearContents(); pasteboard.writeObjects([mixed])
        var capturedImage: Packet?
        clip.onChange = { p in capturedImage = p }
        clip.capture(); try require(capturedImage?.kind == 2 && capturedImage?.body == coloredPNG)
        pasteboard.clearContents(); pasteboard.setData(tiff, forType: .tiff)
        capturedImage = nil; clip.capture()
        try require(capturedImage?.kind == 2 && capturedImage.map { Wire.pngValid($0.body) } == true)
        // File-copy support stays scoped to one ordinary PNG/JPEG image.
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("LightClip-selftest-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let pngURL = folder.appendingPathComponent("fixture.png"), jpgURL = folder.appendingPathComponent("fixture.jpg")
        try coloredPNG.write(to: pngURL)
        try pixels.representation(using: .jpeg, properties: [.compressionFactor: 0.9])!.write(to: jpgURL)
        for url in [pngURL, jpgURL] {
            pasteboard.clearContents(); pasteboard.writeObjects([url as NSURL]); capturedImage = nil; clip.capture()
            try require(capturedImage?.kind == 2 && capturedImage.map { Wire.pngValid($0.body) } == true)
        }
        pasteboard.clearContents(); pasteboard.writeObjects([pngURL as NSURL, jpgURL as NSURL]); capturedImage = nil; clip.capture()
        try require(capturedImage == nil)
        let nonImage = folder.appendingPathComponent("fixture.txt"); try Data("not an image".utf8).write(to: nonImage)
        for url in [nonImage, folder] {
            pasteboard.clearContents(); pasteboard.writeObjects([url as NSURL]); capturedImage = nil; clip.capture()
            try require(capturedImage == nil)
        }
        let concealedFile = NSPasteboardItem()
        concealedFile.setString(pngURL.absoluteString, forType: .fileURL)
        concealedFile.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
        pasteboard.clearContents(); pasteboard.writeObjects([concealedFile]); capturedImage = nil; clip.capture()
        try require(capturedImage == nil)
        clip.onChange = { p in if p.body == original.body { captures += 1 } }
        try clip.apply(original)
        try require(pasteboard.string(forType: .string) == String(data: original.body, encoding: .utf8))
        clip.start()
        try clip.apply(original)
        RunLoop.main.run(until: Date(timeIntervalSinceNow: 1.0))
        clip.stop()
        try require(captures == 1)
        let concealed = NSPasteboardItem()
        concealed.setString(String(data: original.body, encoding: .utf8)!, forType: .string)
        concealed.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
        pasteboard.clearContents(); pasteboard.writeObjects([concealed]); clip.capture()
        try require(captures == 1)
        print("PASS: Swift encryption/validation/replay; isolated text, PNG+TIFF consumers, alpha/color, TIFF→PNG, image priority, single PNG/JPEG file-copy, multi-file/non-image/directory exclusions, echo suppression, concealed exclusion")
    }
    static func interopServer() {
        let board = NSPasteboard.withUniqueName(), clip = ClipBoard(board: board), engine = SyncEngine()
        clip.start()
        var textCount = 0, imageCount = 0, pingCount = 0
        engine.onReceive = { packet in
            try clip.apply(packet)
            if packet.kind == 1 {
                guard board.string(forType: .string) == String(data: packet.body, encoding: .utf8) else { throw ClipError.invalid }
                textCount += 1
            }
            if packet.kind == 2 {
                guard board.data(forType: .png) == packet.body,
                      let tiff = board.data(forType: .tiff), NSBitmapImageRep(data: tiff) != nil,
                      NSImage(pasteboard: board) != nil else { throw ClipError.invalid }
                imageCount += 1
            }
            if packet.kind == 0 { pingCount += 1 }
        }
        engine.onStatus = { value in if value.hasPrefix("已启用") { print("INTEROP_READY"); fflush(stdout) } }
        do { try engine.start(host: "127.0.0.1", key: testKey, loopbackOnly: true, listenPort: interopPort) }
        catch { print("INTEROP_START_FAILED: \(type(of: error))"); exit(1) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 30) {
            engine.stop(); clip.stop(); board.releaseGlobally()
            print("INTEROP_COUNTS text=\(textCount) image=\(imageCount) ping=\(pingCount)")
            fflush(stdout)
            exit(textCount >= 2 && imageCount >= 1 && pingCount >= 1 ? 0 : 1)
        }
        RunLoop.main.run()
    }
    static func interopClient() {
        let engine = SyncEngine()
        var pending = [Packet(kind: 1, body: Data("Mac → .NET 中文 🖥️\nnew line".utf8)), Packet(kind: 2, body: fixturePNG), Packet(kind: 0, body: Data())]
        var sent = 0
        engine.onStatus = { value in
            if value == "连接测试成功" || value.hasSuffix("发送成功") {
                sent += 1
                if pending.isEmpty { engine.stop(); print("PASS: Swift→.NET text/image/ping and authenticated ACK"); exit(0) }
                engine.send(pending.removeFirst())
            } else if value.hasPrefix("已启用") { engine.send(pending.removeFirst()) }
            else if value.hasPrefix("未送达") || value == "对端确认无效" { engine.stop(); print("INTEROP_CLIENT_FAILED"); exit(1) }
        }
        do { try engine.start(host: "127.0.0.1", key: testKey, loopbackOnly: true, listenPort: interopPort + 1, peerPort: interopPort) }
        catch { print("INTEROP_CLIENT_START_FAILED"); exit(1) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 20) { engine.stop(); print("INTEROP_CLIENT_TIMEOUT"); exit(1) }
        RunLoop.main.run()
    }
}
