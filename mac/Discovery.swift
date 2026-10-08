import Darwin
import Foundation

enum DiscoveryError: Error {
    case invalid
}

struct NearbyDevice: Identifiable, Equatable {
    let id: UUID
    let name: String
    let host: String
    let groupID: UUID?
    let lastSeen: Date
}

final class LANDiscovery {
    private let port: UInt16
    init(port: UInt16 = 49286) { self.port = port }
    private static let maximumJSONBytes = 1024
    private static let maximumNameBytes = 96
    private static let maximumDevices = 64
    private static let maximumPacketsPerRead = 64
    private static let deviceExpiry: TimeInterval = 20
    private static let immediateResponseInterval: TimeInterval = 1

    var onUpdate: ([NearbyDevice]) -> Void = { _ in }

    private var deviceID: UUID?
    private var deviceName = ""
    private var groupID: UUID?
    private var socketFD: Int32 = -1
    private var readSource: DispatchSourceRead?
    private var timer: DispatchSourceTimer?
    private var generation = 0
    private var running = false
    private var nearby: [UUID: NearbyDevice] = [:]
    private var lastImmediateResponse: Date?

    func start(deviceID: UUID, name: String, groupID: UUID?) throws {
        requireMainThread()
        guard Self.validName(name) else { throw DiscoveryError.invalid }
        guard let initialData = Self.messageData(deviceID: deviceID, name: name, groupID: groupID),
              initialData.count <= Self.maximumJSONBytes else {
            throw DiscoveryError.invalid
        }

        if running || socketFD >= 0 || readSource != nil || timer != nil {
            stop()
        }

        let fd = try Self.openSocket(port: port)
        self.deviceID = deviceID
        self.deviceName = name
        self.groupID = groupID
        self.socketFD = fd
        self.nearby.removeAll(keepingCapacity: true)
        self.lastImmediateResponse = nil
        self.generation += 1
        let epoch = self.generation
        self.running = true

        let source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: .main)
        source.setEventHandler { [weak self] in
            guard let self, self.running, self.generation == epoch else { return }
            self.readDatagrams(epoch: epoch)
        }
        // The owning main-queue lifecycle closes the descriptor synchronously in
        // stop(). Keeping the cancellation handler empty avoids closing a reused
        // descriptor if a caller stops and starts again in one run-loop turn.
        source.setCancelHandler {}
        self.readSource = source
        source.resume()

        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(
            deadline: .now() + .seconds(5),
            repeating: .seconds(5),
            leeway: .milliseconds(100))
        timer.setEventHandler { [weak self] in
            guard let self, self.running, self.generation == epoch else { return }
            self.removeExpiredDevices(now: Date())
            self.announceNowInternal()
        }
        self.timer = timer
        timer.resume()

        // Announce immediately so a peer does not have to wait for the first timer tick.
        announceNowInternal()
    }

    func update(name: String, groupID: UUID?) {
        requireMainThread()
        guard Self.validName(name),
              let data = Self.messageData(deviceID: deviceID ?? UUID(), name: name, groupID: groupID),
              data.count <= Self.maximumJSONBytes else {
            return
        }

        deviceName = name
        self.groupID = groupID
        if running {
            announceNowInternal()
        }
    }

    func announceNow() {
        requireMainThread()
        guard running else { return }
        announceNowInternal()
    }

    func stop() {
        requireMainThread()
        running = false
        generation += 1

        if let timer {
            timer.setEventHandler(handler: nil)
            timer.cancel()
            self.timer = nil
        }

        if let readSource {
            readSource.setEventHandler(handler: nil)
            let fd = socketFD
            socketFD = -1
            readSource.cancel()
            self.readSource = nil
            if fd >= 0 {
                Darwin.close(fd)
            }
        } else if socketFD >= 0 {
            Darwin.close(socketFD)
            socketFD = -1
        }

        nearby.removeAll(keepingCapacity: true)
        lastImmediateResponse = nil
    }

    private func announceNowInternal() {
        guard running, socketFD >= 0,
              let identity = deviceID,
              let data = Self.messageData(deviceID: identity, name: deviceName, groupID: groupID),
              data.count <= Self.maximumJSONBytes else {
            return
        }

        for address in Self.currentBroadcastAddresses() {
            send(data: data, to: address)
        }
    }

    private func sendAnnouncement(to address: in_addr) {
        guard running, socketFD >= 0,
              let identity = deviceID,
              let data = Self.messageData(deviceID: identity, name: deviceName, groupID: groupID),
              data.count <= Self.maximumJSONBytes else {
            return
        }
        send(data: data, to: address)
    }

    private func send(data: Data, to address: in_addr) {
        guard socketFD >= 0 else { return }
        var destination = Self.sockaddrFor(address, port: port)
        data.withUnsafeBytes { bytes in
            guard let baseAddress = bytes.baseAddress else { return }
            withUnsafePointer(to: &destination) { pointer in
                pointer.withMemoryRebound(to: Darwin.sockaddr.self, capacity: 1) { socketAddress in
                    _ = Darwin.sendto(
                        socketFD,
                        baseAddress,
                        data.count,
                        0,
                        socketAddress,
                        socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
    }

    private func readDatagrams(epoch: Int) {
        guard running, generation == epoch, socketFD >= 0 else { return }
        for _ in 0..<Self.maximumPacketsPerRead {
            guard running, generation == epoch, socketFD >= 0 else { return }
            guard let datagram = receiveDatagram() else { return }
            guard datagram.payload.count <= Self.maximumJSONBytes else { continue }
            handle(datagram.payload, source: datagram.source, epoch: epoch)
        }
    }

    private func receiveDatagram() -> (payload: Data, source: in_addr)? {
        var bytes = [UInt8](repeating: 0, count: Self.maximumJSONBytes * 2)
        var sourceStorage = sockaddr_storage()
        var sourceLength = socklen_t(MemoryLayout<sockaddr_storage>.size)
        let received: Int = bytes.withUnsafeMutableBytes { buffer in
            withUnsafeMutablePointer(to: &sourceStorage) { pointer in
                pointer.withMemoryRebound(to: Darwin.sockaddr.self, capacity: 1) { sourceAddress in
                    Darwin.recvfrom(
                        socketFD,
                        buffer.baseAddress,
                        buffer.count,
                        0,
                        sourceAddress,
                        &sourceLength)
                }
            }
        }

        if received < 0 {
            if errno == EAGAIN || errno == EWOULDBLOCK {
                return nil
            }
            return nil
        }
        guard let source = Self.ipv4Address(from: sourceStorage),
              Self.isAllowedSource(source) else {
            return received == 0 ? nil : (Data(), in_addr())
        }
        return (Data(bytes.prefix(received)), source)
    }

    private func handle(_ payload: Data, source: in_addr, epoch: Int) {
        guard running, generation == epoch, !payload.isEmpty else { return }
        guard let message = Self.decode(payload),
              let identity = deviceID,
              message.id != identity,
              let host = Self.numericString(for: source) else {
            return
        }

        let now = Date()
        let old = nearby[message.id]
        if old == nil && nearby.count >= Self.maximumDevices {
            return
        }

        let device = NearbyDevice(
            id: message.id,
            name: message.name,
            host: host,
            groupID: message.groupID,
            lastSeen: now)
        nearby[message.id] = device

        let visibleChanged = old == nil ||
            old!.name != device.name ||
            old!.host != device.host ||
            old!.groupID != device.groupID
        if visibleChanged {
            emitUpdate()
        }

        if old == nil || old!.host != host {
            respondIfAllowed(to: source, now: now)
        }
    }

    private func respondIfAllowed(to source: in_addr, now: Date) {
        if let lastImmediateResponse,
           now.timeIntervalSince(lastImmediateResponse) < Self.immediateResponseInterval {
            return
        }
        self.lastImmediateResponse = now
        sendAnnouncement(to: source)
    }

    private func removeExpiredDevices(now: Date) {
        let expired = nearby.filter {
            now.timeIntervalSince($0.value.lastSeen) > Self.deviceExpiry
        }.map(\.key)
        guard !expired.isEmpty else { return }
        expired.forEach { nearby.removeValue(forKey: $0) }
        emitUpdate()
    }

    private func emitUpdate() {
        let values = nearby.values.sorted {
            if $0.name != $1.name { return $0.name < $1.name }
            return $0.id.uuidString.lowercased() < $1.id.uuidString.lowercased()
        }
        onUpdate(values)
    }

    private func requireMainThread() {
        dispatchPrecondition(condition: .onQueue(.main))
    }

    private struct DecodedMessage {
        let id: UUID
        let name: String
        let groupID: UUID?
    }

    private struct DiscoveryMessage: Codable {
        let v: Int
        let type: String
        let deviceId: String
        let name: String
        let groupId: String?

        enum CodingKeys: String, CodingKey {
            case v
            case type
            case deviceId
            case name
            case groupId
        }

        func encode(to encoder: Encoder) throws {
            var container = encoder.container(keyedBy: CodingKeys.self)
            try container.encode(v, forKey: .v)
            try container.encode(type, forKey: .type)
            try container.encode(deviceId, forKey: .deviceId)
            try container.encode(name, forKey: .name)
            try container.encodeIfPresent(groupId, forKey: .groupId)
        }
    }

    private static func messageData(deviceID: UUID, name: String, groupID: UUID?) -> Data? {
        guard validName(name) else { return nil }
        let message = DiscoveryMessage(
            v: 2,
            type: "lightclip-discovery",
            deviceId: deviceID.uuidString.lowercased(),
            name: name,
            groupId: groupID?.uuidString.lowercased())
        guard let data = try? JSONEncoder().encode(message), data.count <= maximumJSONBytes else {
            return nil
        }
        return data
    }

    private static func decode(_ data: Data) -> DecodedMessage? {
        guard data.count > 0, data.count <= maximumJSONBytes,
              let message = try? JSONDecoder().decode(DiscoveryMessage.self, from: data),
              message.v == 2,
              message.type == "lightclip-discovery",
              let id = UUID(uuidString: message.deviceId),
              message.deviceId == id.uuidString.lowercased(),
              validName(message.name) else {
            return nil
        }

        let group: UUID?
        if let rawGroup = message.groupId {
            guard let parsed = UUID(uuidString: rawGroup),
                  rawGroup == parsed.uuidString.lowercased() else {
                return nil
            }
            group = parsed
        } else {
            group = nil
        }
        return DecodedMessage(id: id, name: message.name, groupID: group)
    }

    private static func validName(_ name: String) -> Bool {
        guard !name.isEmpty,
              !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              let data = name.data(using: .utf8),
              data.count <= maximumNameBytes else {
            return false
        }
        return !name.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) }
    }

    private static func openSocket(port: UInt16) throws -> Int32 {
        let fd = Darwin.socket(AF_INET, SOCK_DGRAM, 0)
        guard fd >= 0 else { throw DiscoveryError.invalid }

        var option: Int32 = 1
        let optionLength = socklen_t(MemoryLayout<Int32>.size)
        guard Darwin.setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &option, optionLength) == 0,
              Darwin.setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &option, optionLength) == 0 else {
            Darwin.close(fd)
            throw DiscoveryError.invalid
        }

        let flags = Darwin.fcntl(fd, F_GETFL, 0)
        guard flags >= 0, Darwin.fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0 else {
            Darwin.close(fd)
            throw DiscoveryError.invalid
        }

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = UInt8(AF_INET)
        address.sin_port = port.bigEndian
        address.sin_addr = in_addr(s_addr: INADDR_ANY)
        let result = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: Darwin.sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard result == 0 else {
            Darwin.close(fd)
            throw DiscoveryError.invalid
        }
        return fd
    }

    private static func sockaddrFor(_ address: in_addr, port: UInt16) -> sockaddr_in {
        var result = sockaddr_in()
        result.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        result.sin_family = UInt8(AF_INET)
        result.sin_port = port.bigEndian
        result.sin_addr = address
        return result
    }

    private static func ipv4Address(from storage: sockaddr_storage) -> in_addr? {
        var value = storage
        return withUnsafePointer(to: &value) { pointer in
            pointer.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                guard $0.pointee.sin_family == UInt8(AF_INET) else { return nil }
                return $0.pointee.sin_addr
            }
        }
    }

    private static func ipv4Address(from pointer: UnsafeMutablePointer<Darwin.sockaddr>?) -> in_addr? {
        guard let pointer,
              pointer.pointee.sa_family == UInt8(AF_INET) else {
            return nil
        }
        return pointer.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
            $0.pointee.sin_addr
        }
    }

    private static func numericString(for address: in_addr) -> String? {
        var value = address
        var buffer = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        guard inet_ntop(AF_INET, &value, &buffer, socklen_t(buffer.count)) != nil else {
            return nil
        }
        return String(cString: buffer)
    }

    private static func isAllowedSource(_ address: in_addr) -> Bool {
        let hostOrder = UInt32(bigEndian: address.s_addr)
        let firstOctet = hostOrder >> 24
        return firstOctet != 0 && firstOctet != 127 && firstOctet < 224
    }

    private static func currentBroadcastAddresses() -> [in_addr] {
        var interfaces: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&interfaces) == 0 else { return [] }
        defer { freeifaddrs(interfaces) }

        let requiredFlags = UInt32(IFF_UP | IFF_RUNNING | IFF_BROADCAST)
        var result: [in_addr] = []
        var seen = Set<UInt32>()
        var cursor = interfaces
        while let current = cursor {
            defer { cursor = current.pointee.ifa_next }
            let entry = current.pointee
            guard entry.ifa_flags & requiredFlags == requiredFlags,
                  let address = ipv4Address(from: entry.ifa_addr),
                  isAllowedSource(address),
                  let netmask = ipv4Address(from: entry.ifa_netmask) else {
                continue
            }

            let broadcast = ipv4Address(from: entry.ifa_dstaddr) ?? subnetBroadcast(address, netmask)
            let broadcastHostOrder = UInt32(bigEndian: broadcast.s_addr)
            guard broadcastHostOrder != 0, broadcastHostOrder != UInt32.max,
                  seen.insert(broadcast.s_addr).inserted else {
                continue
            }
            result.append(broadcast)
        }
        return result
    }

    private static func subnetBroadcast(_ address: in_addr, _ netmask: in_addr) -> in_addr {
        let addressHostOrder = UInt32(bigEndian: address.s_addr)
        let maskHostOrder = UInt32(bigEndian: netmask.s_addr)
        let broadcastHostOrder = addressHostOrder | ~maskHostOrder
        return in_addr(s_addr: broadcastHostOrder.bigEndian)
    }
}
