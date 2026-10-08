import Foundation
import CryptoKit
import ImageIO

enum ClipError: Error { case invalid, size, authentication, replay, timeout }

struct Packet {
    var id: UUID = UUID()
    var timestamp: UInt64 = UInt64(Date().timeIntervalSince1970 * 1000)
    var kind: UInt8
    var body: Data
}

enum Wire {
    static let port: UInt16 = 49287
    static let maxText = 1024 * 1024
    static let maxImage = 8 * 1024 * 1024
    static let maxEnvelope = maxImage + 29 + 28
    static let aad = Data("LightClip/v1".utf8)
    static func put64(_ value: UInt64) -> Data {
        var n = value.bigEndian
        return withUnsafeBytes(of: &n) { Data($0) }
    }
    static func get64(_ data: Data) -> UInt64 {
        data.reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
    }
    static func pngValid(_ data: Data) -> Bool {
        guard data.count >= 33, data.count <= maxImage,
              data.prefix(8) == Data([137,80,78,71,13,10,26,10]),
              data[8..<12] == Data([0,0,0,13]),
              data[12..<16] == Data("IHDR".utf8) else { return false }
        let w = get64(data[16..<20]), h = get64(data[20..<24])
        guard w > 0, h > 0, w <= 16_000_000, h <= 16_000_000,
              w * h <= 16_000_000,
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              CGImageSourceGetCount(source) > 0 else { return false }
        return CGImageSourceCopyPropertiesAtIndex(source, 0, nil) != nil
    }
    static func validate(_ p: Packet, now: UInt64 = UInt64(Date().timeIntervalSince1970 * 1000)) throws {
        guard abs(Double(p.timestamp) - Double(now)) <= 120_000 else { throw ClipError.invalid }
        switch p.kind {
        case 0, 3: guard p.body.isEmpty else { throw ClipError.invalid }
        case 1: guard p.body.count <= maxText, String(data: p.body, encoding: .utf8) != nil else { throw ClipError.invalid }
        case 2: guard pngValid(p.body) else { throw ClipError.invalid }
        default: throw ClipError.invalid
        }
    }
    static func seal(_ packet: Packet, key: Data) throws -> Data {
        guard key.count == 32 else { throw ClipError.authentication }
        try validate(packet)
        var data = Data("LCP1".utf8)
        var uuid = packet.id.uuid
        data.append(withUnsafeBytes(of: &uuid) { Data($0) })
        data.append(put64(packet.timestamp))
        data.append(packet.kind)
        data.append(packet.body)
        let box = try AES.GCM.seal(data, using: SymmetricKey(data: key), authenticating: aad)
        guard let combined = box.combined else { throw ClipError.authentication }
        return combined
    }
    static func open(_ envelope: Data, key: Data) throws -> Packet {
        guard key.count == 32, envelope.count >= 57, envelope.count <= maxEnvelope else { throw ClipError.size }
        let box = try AES.GCM.SealedBox(combined: envelope)
        let data = try AES.GCM.open(box, using: SymmetricKey(data: key), authenticating: aad)
        guard data.count >= 29, data.prefix(4) == Data("LCP1".utf8) else { throw ClipError.invalid }
        let b = Array(data[4..<20])
        let id = UUID(uuid: (b[0],b[1],b[2],b[3],b[4],b[5],b[6],b[7],b[8],b[9],b[10],b[11],b[12],b[13],b[14],b[15]))
        let packet = Packet(id: id, timestamp: get64(data[20..<28]), kind: data[28], body: Data(data.dropFirst(29)))
        try validate(packet)
        return packet
    }
    static func frame(_ envelope: Data) -> Data {
        var length = UInt32(envelope.count).bigEndian
        var data = withUnsafeBytes(of: &length) { Data($0) }
        data.append(envelope)
        return data
    }
}

final class ReplayGuard {
    private var entries: [UUID: Date] = [:]
    func accept(_ id: UUID) -> Bool {
        let now = Date()
        entries = entries.filter { now.timeIntervalSince($0.value) < 300 }
        guard entries[id] == nil else { return false }
        // Refuse excess requests rather than evicting still-live replay protection.
        guard entries.count < 4096 else { return false }
        entries[id] = now
        return true
    }
}
