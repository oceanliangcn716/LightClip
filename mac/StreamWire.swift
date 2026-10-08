import Foundation
import CryptoKit

struct StreamItem: Codable, Equatable { let name: String; let size: UInt64 }
struct StreamOffer: Codable, Equatable {
    var v = 2
    let type: String
    let items: [StreamItem]
    let total: UInt64
    func validate() throws {
        guard v == 2, type == "files" || type == "text", (1...32).contains(items.count) else { throw ClipError.invalid }
        var sum: UInt64 = 0, names = Set<String>()
        for item in items {
            guard StreamWire.validName(item.name), item.size <= StreamWire.maxFiles,
                  names.insert(item.name.lowercased()).inserted else { throw ClipError.invalid }
            let result = sum.addingReportingOverflow(item.size)
            guard !result.overflow else { throw ClipError.size }; sum = result.partialValue
        }
        guard sum == total else { throw ClipError.invalid }
        if type == "text" {
            guard items.count == 1, items[0].name == "clipboard.txt", total <= UInt64(StreamWire.maxText) else { throw ClipError.size }
        } else { guard total <= StreamWire.maxFiles else { throw ClipError.size } }
    }
    func bytes() throws -> Data {
        try validate()
        let result = try JSONEncoder().encode(self)
        guard result.count <= 16_384 else { throw ClipError.size }; return result
    }
    static func read(_ data: Data) throws -> StreamOffer {
        guard data.count <= 16_384 else { throw ClipError.size }
        let result = try JSONDecoder().decode(Self.self, from: data); try result.validate(); return result
    }
}
struct StreamRecord {
    let id: UUID
    let sequence: UInt32
    let kind: UInt8
    var body = Data()
    var timestamp: UInt64 = UInt64(Date().timeIntervalSince1970 * 1000)
}
enum StreamWire {
    static let port: UInt16 = 49289
    static let maxText = 10 * 1024 * 1024
    static let maxFiles: UInt64 = 5 * 1024 * 1024 * 1024
    static let chunkBytes = 256 * 1024
    static let maxEnvelope = 262_217
    static let aad = Data("LightClip/stream/v2".utf8)
    static func validName(_ value: String) -> Bool {
        guard !value.isEmpty, value != ".", value != "..", value.utf8.count <= 255,
              !value.hasSuffix("."), !value.hasSuffix(" "),
              !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) || "/\\:*?\"<>|".unicodeScalars.contains($0) }) else { return false }
        let base = value.split(separator: ".", omittingEmptySubsequences: false).first.map(String.init)?.uppercased() ?? ""
        let reserved = ["CON", "PRN", "AUX", "NUL"] + (1...9).flatMap { ["COM\($0)", "LPT\($0)"] }
        return !reserved.contains(base)
    }
    static func put32(_ value: UInt32) -> Data { var value = value.bigEndian; return withUnsafeBytes(of: &value) { Data($0) } }
    static func seal(_ value: StreamRecord, key: Data) throws -> Data {
        guard key.count == 32, (10...15).contains(value.kind), value.body.count <= chunkBytes + 12 else { throw ClipError.invalid }
        let plain = Data("LCS2".utf8) + PairCrypto.uuid(value.id) + Wire.put64(value.timestamp) + put32(value.sequence) + Data([value.kind]) + value.body
        let box = try AES.GCM.seal(plain, using: SymmetricKey(data: key), authenticating: aad)
        guard let result = box.combined else { throw ClipError.authentication }; return Wire.frame(result)
    }
    static func open(_ envelope: Data, key: Data) throws -> StreamRecord {
        guard key.count == 32, envelope.count >= 61, envelope.count <= maxEnvelope else { throw ClipError.size }
        let plain = try AES.GCM.open(AES.GCM.SealedBox(combined: envelope), using: SymmetricKey(data: key), authenticating: aad)
        guard plain.count >= 33, plain.prefix(4) == Data("LCS2".utf8) else { throw ClipError.invalid }
        let b = Array(plain[4..<20])
        let id = UUID(uuid: (b[0],b[1],b[2],b[3],b[4],b[5],b[6],b[7],b[8],b[9],b[10],b[11],b[12],b[13],b[14],b[15]))
        let timestamp = Wire.get64(plain[20..<28]), kind = plain[32]
        guard abs(Double(timestamp) - Date().timeIntervalSince1970 * 1000) <= 120_000, (10...15).contains(kind) else { throw ClipError.invalid }
        return StreamRecord(id: id, sequence: UInt32(Wire.get64(plain[28..<32])), kind: kind, body: Data(plain.dropFirst(33)), timestamp: timestamp)
    }
}

// Validate UTF-8 across chunk boundaries without constructing a full String.
struct UTF8StreamValidator {
    private var remaining = 0
    private var lower: UInt8 = 0x80, upper: UInt8 = 0xbf
    mutating func consume(_ data: Data) throws {
        for byte in data {
            if remaining > 0 {
                guard byte >= lower && byte <= upper else { throw ClipError.invalid }
                remaining -= 1; lower = 0x80; upper = 0xbf
            } else if byte < 0x80 { continue }
            else if (0xc2...0xdf).contains(byte) { remaining = 1 }
            else if (0xe0...0xef).contains(byte) { remaining = 2; lower = byte == 0xe0 ? 0xa0 : 0x80; upper = byte == 0xed ? 0x9f : 0xbf }
            else if (0xf0...0xf4).contains(byte) { remaining = 3; lower = byte == 0xf0 ? 0x90 : 0x80; upper = byte == 0xf4 ? 0x8f : 0xbf }
            else { throw ClipError.invalid }
        }
    }
    var complete: Bool { remaining == 0 }
}
