import AppKit
import Network
import CryptoKit
import Darwin

enum GroupTest {
    // Public synthetic fixtures, not any user's real pairing or group.
    static let clientID = UUID(uuidString: "00112233-4455-6677-8899-aabbccddeeff")!
    static let hostID = UUID(uuidString: "11111111-2222-3333-4444-555555555555")!
    static let groupID = UUID(uuidString: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")!
    static let code = "12345678"
    static let group = SyncGroup(id: groupID, key: SelfTest.testKey)
    static func require(_ value: @autoclosure () -> Bool, _ label: String) throws {
        if !value() { fputs("GROUP_CHECK_FAILED: \(label)\n", stderr); throw ClipError.invalid }
    }
    static func wait(_ label: String, seconds: TimeInterval = 6, until condition: () -> Bool) throws {
        let deadline = Date(timeIntervalSinceNow: seconds)
        while !condition(), Date() < deadline { RunLoop.main.run(until: Date(timeIntervalSinceNow: 0.01)) }
        try require(condition(), label)
    }
    static func run() throws {
        let a = try SpakeContext(role: 0, code: code, client: clientID, host: hostID, group: groupID)
        let b = try SpakeContext(role: 1, code: code, client: clientID, host: hostID, group: groupID)
        let keyA = try a.finish(b.message), keyB = try b.finish(a.message)
        try require(keyA == keyB, "SPAKE agreement")
        try SelfTest.rejected { _ = try a.finish(b.message) }
        let salt = Data(0..<16)
        let transcript = try PairCrypto.transcript(client: clientID, host: hostID, group: groupID, a: a.message, b: b.message, salt: salt)
        try require(transcript.count == 148, "transcript size")
        let key = PairCrypto.derive(keyA, salt: salt, transcript: transcript)
        let proof = PairCrypto.proof(key, label: "server-confirm", transcript: transcript)
        try require(PairCrypto.verify(proof, key: key, label: "server-confirm", transcript: transcript), "confirmation")
        try require(!PairCrypto.verify(proof, key: key, label: "client-confirm", transcript: transcript), "role separation")
        var wrapped = try PairCrypto.wrap(group, key: key, transcript: transcript)
        let unwrapped = try PairCrypto.unwrap(wrapped, expected: groupID, key: key, transcript: transcript)
        try require(unwrapped.key == group.key, "group unwrap")
        try SelfTest.rejected { _ = try PairCrypto.unwrap(wrapped, expected: UUID(), key: key, transcript: transcript) }
        wrapped[20] ^= 1
        try SelfTest.rejected { _ = try PairCrypto.unwrap(wrapped, expected: groupID, key: key, transcript: transcript) }
        try pairingNetwork()
        try fanout()
        try discoveryNetwork()
        print("PASS: group PAKE/confirmation/tamper, actual pairing success/wrong/expired codes, multi-peer text/image fanout, partial delivery, endpoint change, UDP discovery/source-IP/validation/expiry")
    }
    static func pairingNetwork() throws {
        let host = PairingService(codeGenerator: { code })
        let client = PairingService()
        defer { host.stop(); client.stop() }
        var ready = false; host.onReady = { ready = true }
        try host.start(deviceID: hostID, name: "Mac 合成测试", group: group, port: 49389, loopbackOnly: true)
        try wait("pair server ready", until: { ready })
        _ = try host.openInvitation()
        let peer = NearbyDevice(id: hostID, name: "Mac 合成测试", host: "127.0.0.1", groupID: groupID, lastSeen: Date())
        var success: Bool?, saved: SyncGroup?
        client.join(device: peer, code: "12345679", deviceID: clientID, name: "加入者测试", port: 49389, persist: { saved = $0 }, completion: { success = $0 })
        try wait("wrong code rejected", until: { success != nil })
        try require(success == false && saved == nil, "wrong code never saved")
        _ = try host.openInvitation(); success = nil
        client.join(device: peer, code: code, deviceID: clientID, name: "加入者测试", port: 49389, persist: { saved = $0 }, completion: { success = $0 })
        try wait("pair success", until: { success != nil })
        try require(success == true && saved?.key == group.key && saved?.id == groupID && host.invitationExpiry == nil, "pair persistence and one-use invitation")
        host.stop()
        let expiring = PairingService(codeGenerator: { code }, invitationLifetime: 0.02)
        defer { expiring.stop() }
        ready = false; expiring.onReady = { ready = true }
        try expiring.start(deviceID: hostID, name: "Mac 合成测试", group: group, port: 49389, loopbackOnly: true)
        try wait("expiry server ready", until: { ready })
        _ = try expiring.openInvitation()
        RunLoop.main.run(until: Date(timeIntervalSinceNow: 0.04))
        success = nil; saved = nil
        client.join(device: peer, code: code, deviceID: clientID, name: "加入者测试", port: 49389, persist: { saved = $0 }, completion: { success = $0 })
        try wait("expired code rejected", until: { success != nil })
        try require(success == false && saved == nil, "expired code never saved")
    }
    static func fanout() throws {
        let sender = SyncEngine(), first = SyncEngine(), second = SyncEngine()
        let pb1 = NSPasteboard.withUniqueName(), pb2 = NSPasteboard.withUniqueName()
        let clip1 = ClipBoard(board: pb1), clip2 = ClipBoard(board: pb2)
        defer { sender.stop(); first.stop(); second.stop(); clip1.stop(); clip2.stop(); pb1.releaseGlobally(); pb2.releaseGlobally() }
        var firstCount = 0, secondCount = 0, echoes = 0, ready = 0, status = ""
        clip1.onChange = { _ in echoes += 1 }; clip2.onChange = { _ in echoes += 1 }
        clip1.start(); clip2.start()
        first.onReceive = { try clip1.apply($0); if $0.kind != 0 { firstCount += 1 } }
        second.onReceive = { try clip2.apply($0); if $0.kind != 0 { secondCount += 1 } }
        first.onStatus = { if $0.hasPrefix("已启用") { ready += 1 } }
        second.onStatus = { if $0.hasPrefix("已启用") { ready += 1 } }
        sender.onStatus = { status = $0; if $0.hasPrefix("已启用") { ready += 1 } }
        try first.start(host: "", key: group.key, loopbackOnly: true, listenPort: 49391)
        try second.start(host: "", key: group.key, loopbackOnly: true, listenPort: 49392)
        try sender.start(host: "", key: group.key, loopbackOnly: true, listenPort: 49390)
        try wait("fanout listeners ready", until: { ready == 3 })
        sender.updateEndpoints([PeerEndpoint(host: "127.0.0.1", port: 49391), PeerEndpoint(host: "127.0.0.1", port: 49392)])
        let text = Packet(kind: 1, body: Data("群组文字 中文 🖥️".utf8))
        sender.send(text)
        try wait("text to two peers", until: { firstCount == 1 && secondCount == 1 && status.contains("发送成功") })
        try require(pb1.string(forType: .string) == pb2.string(forType: .string), "both pasted text equal")
        sender.send(Packet(kind: 2, body: SelfTest.fixturePNG))
        try wait("image to two peers", until: { firstCount == 2 && secondCount == 2 && status.contains("发送成功") })
        try require(NSImage(pasteboard: pb1) != nil && NSImage(pasteboard: pb2) != nil, "both image consumers")
        second.stop(); sender.send(Packet(kind: 1, body: Data("partial delivery".utf8)))
        try wait("offline peer partial delivery", until: { firstCount == 3 && status.contains("1/2") })
        try require(secondCount == 2, "offline clipboard unchanged")
        ready = 0
        try second.start(host: "", key: group.key, loopbackOnly: true, listenPort: 49393)
        try wait("moved peer listener ready", until: { ready == 1 })
        sender.updateEndpoints([PeerEndpoint(host: "127.0.0.1", port: 49391), PeerEndpoint(host: "127.0.0.1", port: 49393)])
        sender.send(Packet(kind: 1, body: Data("new destination".utf8)))
        try wait("updated endpoint fanout", until: { firstCount == 4 && secondCount == 3 && status.contains("发送成功") })
        RunLoop.main.run(until: Date(timeIntervalSinceNow: 0.85))
        try require(echoes == 0, "remote writes never echoed")
        sender.stop(); sender.send(Packet(kind: 1, body: Data("paused".utf8)))
        RunLoop.main.run(until: Date(timeIntervalSinceNow: 0.1))
        try require(firstCount == 4 && secondCount == 3, "paused sends nothing")
        first.stop(); ready = 0
        try first.start(host: "", key: group.key, loopbackOnly: true, listenPort: 49391)
        try sender.start(host: "127.0.0.1", key: group.key, loopbackOnly: true, listenPort: 49390, peerPort: 49391)
        try wait("resume listeners ready", until: { ready == 2 })
        sender.send(text)
        try wait("replay after resume rejected", until: { status.hasPrefix("未送达") })
        try require(firstCount == 4, "pause/resume preserves replay guard")
    }
    static func discoveryNetwork() throws {
        let discovery = LANDiscovery(port: 49386)
        defer { discovery.stop() }
        var snapshots: [NearbyDevice] = []
        discovery.onUpdate = { snapshots = $0 }
        try discovery.start(deviceID: hostID, name: "隔离发现测试", groupID: nil)
        let address = localAddresses().components(separatedBy: "   ").first?.components(separatedBy: ": ").last ?? ""
        var dest = sockaddr_in(); dest.sin_len = UInt8(MemoryLayout<sockaddr_in>.size); dest.sin_family = UInt8(AF_INET); dest.sin_port = UInt16(49386).bigEndian
        try require(inet_pton(AF_INET, address, &dest.sin_addr) == 1, "local IPv4 available for UDP test")
        let fd = Darwin.socket(AF_INET, SOCK_DGRAM, 0); try require(fd >= 0, "UDP sender socket")
        defer { Darwin.close(fd) }
        func send(_ object: [String: Any]) throws {
            let data = try JSONSerialization.data(withJSONObject: object)
            data.withUnsafeBytes { bytes in withUnsafePointer(to: &dest) { pointer in pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { _ = Darwin.sendto(fd, bytes.baseAddress, data.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } } }
        }
        let ad: [String: Any] = ["v": 2, "type": "lightclip-discovery", "deviceId": clientID.uuidString.lowercased(), "name": "甜甜的测试香蕉", "ip": "8.8.8.8"]
        try send(ad)
        try wait("UDP discovered device", until: { snapshots.count == 1 })
        try require(snapshots[0].host == address && snapshots[0].groupID == nil, "UDP uses real source address")
        var changed = ad; changed["groupId"] = groupID.uuidString.lowercased(); changed["name"] = "换名字的测试香蕉"
        try send(changed)
        try wait("UDP group/name update", until: { snapshots.first?.groupID == groupID && snapshots.first?.name == "换名字的测试香蕉" })
        var invalid = ad; invalid["v"] = 1; invalid["deviceId"] = UUID().uuidString.lowercased(); try send(invalid)
        invalid["v"] = 2; invalid["name"] = String(repeating: "x", count: 1100); try send(invalid)
        RunLoop.main.run(until: Date(timeIntervalSinceNow: 0.1))
        try require(snapshots.count == 1, "malformed/oversized discovery rejected")
        try wait("UDP peer expiry", seconds: 26, until: { snapshots.isEmpty })
    }
    static func pairServer() {
        let server = PairingService(codeGenerator: { code })
        server.onReady = { print("PAIR_READY"); fflush(stdout) }
        server.onStatus = { value in
            if value == "设备已加入群组" { server.stop(); print("PASS: .NET→Swift native PAKE, proofs and group key exchange"); exit(0) }
        }
        do { try server.start(deviceID: hostID, name: "Mac 合成测试", group: group, port: 49388, loopbackOnly: true); _ = try server.openInvitation() }
        catch { print("PAIR_SERVER_FAILED"); exit(1) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 15) { server.stop(); print("PAIR_SERVER_TIMEOUT"); exit(1) }
        RunLoop.main.run()
    }
    static func pairClient() {
        let client = PairingService()
        let peer = NearbyDevice(id: hostID, name: "Windows 合成测试", host: "127.0.0.1", groupID: groupID, lastSeen: Date())
        client.join(device: peer, code: code, deviceID: clientID, name: "Mac 合成测试", port: 49388, persist: { received in
            guard received.id == groupID, received.key == group.key else { throw ClipError.authentication }
        }, completion: { success in print(success ? "PASS: Swift→.NET native PAKE, proofs and group key exchange" : "PAIR_CLIENT_FAILED"); exit(success ? 0 : 1) })
        DispatchQueue.main.asyncAfter(deadline: .now() + 10) { client.stop(); print("PAIR_CLIENT_TIMEOUT"); exit(1) }
        RunLoop.main.run()
    }
}
