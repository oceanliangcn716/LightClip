import Foundation
import CryptoKit
import Darwin

struct FileIdentity: Equatable {
    let device: Int32, inode: UInt64, size: UInt64, seconds: Int, nanos: Int
    init(_ info: stat) throws {
        guard info.st_mode & S_IFMT == S_IFREG, info.st_size >= 0 else { throw ClipError.invalid }
        device = info.st_dev; inode = info.st_ino; size = UInt64(info.st_size)
        seconds = info.st_mtimespec.tv_sec; nanos = info.st_mtimespec.tv_nsec
    }
    static func at(_ url: URL) throws -> FileIdentity {
        guard url.isFileURL, url.host == nil || url.host == "" || url.host == "localhost" else { throw ClipError.invalid }
        var info = stat(); guard lstat(url.path, &info) == 0 else { throw ClipError.invalid }; return try FileIdentity(info)
    }
    static func of(_ handle: FileHandle) throws -> FileIdentity {
        var info = stat(); guard fstat(handle.fileDescriptor, &info) == 0 else { throw ClipError.invalid }; return try FileIdentity(info)
    }
}
struct StreamSourcePlan {
    let offer: StreamOffer
    let urls: [URL]
    let identities: [FileIdentity]
    var ownedDirectory: URL?
    static func files(_ urls: [URL], type: String = "files") throws -> StreamSourcePlan {
        guard (1...32).contains(urls.count) else { throw ClipError.size }
        let identities = try urls.map { try FileIdentity.at($0) }
        var total: UInt64 = 0
        for identity in identities {
            let result = total.addingReportingOverflow(identity.size)
            guard !result.overflow, result.partialValue <= StreamWire.maxFiles else { throw ClipError.size }; total = result.partialValue
        }
        let items = zip(urls, identities).map { StreamItem(name: $0.0.lastPathComponent, size: $0.1.size) }
        let offer = StreamOffer(type: type, items: items, total: total); try offer.validate()
        return StreamSourcePlan(offer: offer, urls: urls, identities: identities)
    }
    static func text(_ text: String, root: URL) throws -> StreamSourcePlan {
        guard text.utf8.count <= StreamWire.maxText else { throw ClipError.size }
        let directory = root.appendingPathComponent(UUID().uuidString + ".outgoing", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        do {
            let url = directory.appendingPathComponent("clipboard.txt")
            let fd = Darwin.open(url.path, O_CREAT | O_EXCL | O_WRONLY | O_NOFOLLOW, 0o600)
            guard fd >= 0 else { throw ClipError.invalid }
            let file = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
            try file.write(contentsOf: Data(text.utf8)); try file.close()
            var result = try files([url], type: "text"); result.ownedDirectory = directory; return result
        } catch { try? FileManager.default.removeItem(at: directory); throw error }
    }
}
final class StreamSourceReader {
    let plan: StreamSourcePlan
    private var handles: [FileHandle] = []
    private var hashes: [SHA256]
    private var index = 0
    private var offset: UInt64 = 0
    private(set) var sent: UInt64 = 0
    init(_ plan: StreamSourcePlan) throws {
        self.plan = plan; hashes = plan.urls.map { _ in SHA256() }
        for (i, url) in plan.urls.enumerated() {
            guard try FileIdentity.at(url) == plan.identities[i] else { throw ClipError.invalid }
            let fd = Darwin.open(url.path, O_RDONLY | O_NOFOLLOW)
            guard fd >= 0 else { throw ClipError.invalid }
            let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true); handles.append(handle)
            guard try FileIdentity.of(handle) == plan.identities[i] else { throw ClipError.invalid }
        }
    }
    func next() throws -> Data? {
        while index < handles.count, offset == plan.identities[index].size {
            guard try FileIdentity.of(handles[index]) == plan.identities[index],
                  try FileIdentity.at(plan.urls[index]) == plan.identities[index] else { throw ClipError.invalid }
            index += 1; offset = 0
        }
        guard index < handles.count else { return nil }
        let count = Int(min(UInt64(StreamWire.chunkBytes), plan.identities[index].size - offset))
        guard let bytes = try handles[index].read(upToCount: count), !bytes.isEmpty else { throw ClipError.invalid }
        let result = StreamWire.put32(UInt32(index)) + Wire.put64(offset) + bytes
        hashes[index].update(data: bytes); offset += UInt64(bytes.count); sent += UInt64(bytes.count)
        return result
    }
    func digests() throws -> Data {
        guard index == handles.count else { throw ClipError.invalid }
        return hashes.reduce(into: Data()) { $0.append(contentsOf: $1.finalize()) }
    }
    deinit { handles.forEach { try? $0.close() } }
}
struct ReceivedStream {
    let offer: StreamOffer
    let urls: [URL]
    let directory: URL
}
final class StreamStorage {
    let root: URL
    let queue = DispatchQueue(label: "cn.oceanliang.lightclip.files", qos: .utility)
    private var reservations: [UUID: UInt64] = [:]
    static let budget: UInt64 = 10 * 1024 * 1024 * 1024
    init(root: URL = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("cn.oceanliang.lightclip/Transfers", isDirectory: true)) { self.root = root }
    func cleanAbandonedTemporaryFiles() {
        guard (try? root.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) != true,
              let children = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey]) else { return }
        for child in children where ["partial", "outgoing"].contains(child.pathExtension) {
            guard UUID(uuidString: child.deletingPathExtension().lastPathComponent) != nil,
                  let values = try? child.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]),
                  values.isDirectory == true, values.isSymbolicLink != true else { continue }
            try? FileManager.default.removeItem(at: child)
        }
    }
    func reserve(_ id: UUID, size: UInt64) throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        guard try root.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink != true else { throw ClipError.invalid }
        var used: UInt64 = reservations.values.reduce(0, +)
        for directory in try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) where UUID(uuidString: directory.lastPathComponent) != nil {
            guard let entries = FileManager.default.enumerator(at: directory, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey]) else { continue }
            for case let file as URL in entries {
                let values = try file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
                if values.isSymbolicLink == true { entries.skipDescendants(); continue }
                if values.isRegularFile == true {
                    let size = UInt64(max(0, values.fileSize ?? 0))
                    guard used <= Self.budget, size <= Self.budget - used else { throw ClipError.size }; used += size
                }
            }
        }
        let free = (try FileManager.default.attributesOfFileSystem(forPath: root.path)[.systemFreeSize] as? NSNumber)?.uint64Value ?? 0
        guard size <= Self.budget, used <= Self.budget - size, free >= size + 64 * 1024 * 1024 else { throw ClipError.size }
        reservations[id] = size
    }
    func release(_ id: UUID) { reservations.removeValue(forKey: id) }
}
final class StreamIncomingFiles {
    let offer: StreamOffer
    private let store: StreamStorage
    private let id: UUID
    private var directory: URL
    private var handles: [FileHandle] = []
    private var hashes: [SHA256]
    private var textValidator = UTF8StreamValidator()
    private var index = 0
    private var offset: UInt64 = 0
    private var retained = false
    private var released = false
    private(set) var received: UInt64 = 0
    init(offer: StreamOffer, store: StreamStorage, id: UUID) throws {
        try offer.validate(); self.offer = offer; self.store = store; self.id = id
        hashes = offer.items.map { _ in SHA256() }
        directory = store.root.appendingPathComponent(UUID().uuidString + ".partial", isDirectory: true)
        try store.reserve(id, size: offer.total)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            for item in offer.items {
                let fd = Darwin.open(directory.appendingPathComponent(item.name).path, O_CREAT | O_EXCL | O_WRONLY | O_NOFOLLOW, 0o600)
                guard fd >= 0 else { throw ClipError.invalid }; handles.append(FileHandle(fileDescriptor: fd, closeOnDealloc: true))
            }
        } catch { discard(); throw error }
    }
    func append(_ body: Data) throws {
        guard body.count > 12, body.count <= StreamWire.chunkBytes + 12 else { throw ClipError.invalid }
        while index < offer.items.count, offset == offer.items[index].size { index += 1; offset = 0 }
        let itemIndex = Int(Wire.get64(body.prefix(4))), itemOffset = Wire.get64(body[4..<12]), data = Data(body.dropFirst(12))
        guard index < offer.items.count, itemIndex == index, itemOffset == offset,
              UInt64(data.count) <= offer.items[index].size - offset else { throw ClipError.invalid }
        if offer.type == "text" { try textValidator.consume(data) }
        try handles[index].write(contentsOf: data); hashes[index].update(data: data)
        offset += UInt64(data.count); received += UInt64(data.count)
    }
    func finish(_ digests: Data) throws -> ReceivedStream {
        while index < offer.items.count, offset == offer.items[index].size { index += 1; offset = 0 }
        guard index == offer.items.count, received == offer.total, digests.count == offer.items.count * 32,
              offer.type != "text" || textValidator.complete else { throw ClipError.invalid }
        for i in hashes.indices {
            guard Data(hashes[i].finalize()) == digests[(i * 32)..<(i * 32 + 32)] else { throw ClipError.authentication }
        }
        for handle in handles { try handle.synchronize(); try handle.close() }; handles.removeAll()
        let finished = store.root.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.moveItem(at: directory, to: finished); directory = finished
        return ReceivedStream(offer: offer, urls: offer.items.map { directory.appendingPathComponent($0.name) }, directory: directory)
    }
    func keep() {
        if offer.type == "text" { discard() }
        else { retained = true; release() }
    }
    private func release() { if !released { store.release(id); released = true } }
    func discard() {
        handles.forEach { try? $0.close() }; handles.removeAll()
        if !retained { try? FileManager.default.removeItem(at: directory) }
        release()
    }
    deinit { discard() }
}
