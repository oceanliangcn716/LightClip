import Foundation
import Network
import CryptoKit
import Security

struct SyncGroup {
    let id: UUID
    let key: Data
}

final class SpakeContext {
    private var state: UnsafeMutableRawPointer?
    let message: Data
    init(role: UInt8, code: String, client: UUID, host: UUID, group: UUID) throws {
        guard code.utf8.count == 8, code.utf8.allSatisfy({ $0 >= 48 && $0 <= 57 }) else { throw ClipError.invalid }
        let password = Array(code.utf8)
        let a = Array("LightClip/pair/v2/client/\(client.uuidString.lowercased())".utf8)
        let b = Array("LightClip/pair/v2/host/\(host.uuidString.lowercased())/\(group.uuidString.lowercased())".utf8)
        var output = [UInt8](repeating: 0, count: 33)
        state = lc_spake_start(role, password, password.count, a, a.count, b, b.count, &output)
        guard state != nil else { throw ClipError.invalid }
        message = Data(output)
    }
    func finish(_ peer: Data) throws -> Data {
        guard let pointer = state else { throw ClipError.invalid }
        state = nil
        var output = [UInt8](repeating: 0, count: 32)
        guard lc_spake_finish(pointer, Array(peer), peer.count, &output) == 1 else { throw ClipError.authentication }
        return Data(output)
    }
    deinit { if let state { lc_spake_destroy(state) } }
}

enum PairCrypto {
    static func uuid(_ id: UUID) -> Data { var value = id.uuid; return withUnsafeBytes(of: &value) { Data($0) } }
    static func transcript(client: UUID, host: UUID, group: UUID, a: Data, b: Data, salt: Data) throws -> Data {
        guard a.count == 33, b.count == 33, salt.count == 16 else { throw ClipError.invalid }
        return Data("LightClip/pair/v2\0".utf8) + uuid(client) + uuid(host) + uuid(group) + a + b + salt
    }
    static func derive(_ secret: Data, salt: Data, transcript: Data) -> SymmetricKey {
        HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: secret), salt: salt, info: transcript, outputByteCount: 32)
    }
    static func proof(_ key: SymmetricKey, label: String, transcript: Data) -> Data {
        Data(HMAC<SHA256>.authenticationCode(for: Data((label + "\0").utf8) + transcript, using: key))
    }
    static func verify(_ proof: Data, key: SymmetricKey, label: String, transcript: Data) -> Bool {
        HMAC<SHA256>.isValidAuthenticationCode(proof, authenticating: Data((label + "\0").utf8) + transcript, using: key)
    }
    static func wrap(_ group: SyncGroup, key: SymmetricKey, transcript: Data) throws -> Data {
        guard group.key.count == 32 else { throw ClipError.invalid }
        let box = try AES.GCM.seal(uuid(group.id) + group.key, using: key, authenticating: Data("LightClip/group-key/v2\0".utf8) + transcript)
        guard let result = box.combined else { throw ClipError.invalid }; return result
    }
    static func unwrap(_ sealed: Data, expected: UUID, key: SymmetricKey, transcript: Data) throws -> SyncGroup {
        guard sealed.count == 76 else { throw ClipError.invalid }
        let body = try AES.GCM.open(AES.GCM.SealedBox(combined: sealed), using: key, authenticating: Data("LightClip/group-key/v2\0".utf8) + transcript)
        guard body.count == 48, body.prefix(16) == uuid(expected) else { throw ClipError.authentication }
        return SyncGroup(id: expected, key: Data(body.suffix(32)))
    }
    static func random(_ count: Int) throws -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        guard SecRandomCopyBytes(kSecRandomDefault, count, &bytes) == errSecSuccess else { throw ClipError.invalid }
        return Data(bytes)
    }
    static func code() throws -> String {
        while true {
            let n = Wire.get64(try random(4))
            if n < 4_200_000_000 { return String(format: "%08u", UInt32(n % 100_000_000)) }
        }
    }
}

private struct PairMessage: Codable {
    var v = 2
    let type: String
    var deviceId: String?
    var name: String?
    var groupId: String?
    var spake: String?
    var salt: String?
    var proof: String?
    var sealed: String?
    func bytes() throws -> Data {
        let data = try JSONEncoder().encode(self)
        guard data.count <= 4096 else { throw ClipError.size }; return Wire.frame(data)
    }
    static func read(_ session: Session, _ done: @escaping (PairMessage) throws -> Void) {
        session.readFrame(minimum: 1, maximum: 4096) { data in
            do {
                let message = try JSONDecoder().decode(PairMessage.self, from: data)
                guard message.v == 2 else { throw ClipError.invalid }
                try done(message)
            } catch { session.finish() }
        }
    }
}

final class PairingService {
    static let port: UInt16 = 49288
    var onStatus: (String) -> Void = { _ in }
    var onReady: () -> Void = {}
    var onInvitationClosed: () -> Void = {}
    private var listener: ListeningSocket?
    private var sessions: [UUID: Session] = [:]
    private var joining: Session?
    private var generation = 0
    private var invitation: (code: String, expiry: Date, attempts: Int)?
    private var localID = UUID()
    private var name = ""
    private var group: SyncGroup?
    private let makeCode: () throws -> String
    private let invitationLifetime: TimeInterval
    init(codeGenerator: @escaping () throws -> String = PairCrypto.code, invitationLifetime: TimeInterval = 120) {
        makeCode = codeGenerator; self.invitationLifetime = min(120, max(0.01, invitationLifetime))
    }
    var invitationExpiry: Date? { invitation?.expiry }
    func start(deviceID: UUID, name: String, group: SyncGroup, port: UInt16 = PairingService.port, loopbackOnly: Bool = false) throws {
        stop()
        self.localID = deviceID; self.name = name; self.group = group
        let parameters = NWParameters.tcp
        parameters.allowLocalEndpointReuse = true
        if loopbackOnly { parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!) }
        let listener = ListeningSocket()
        self.listener = listener
        let epoch = generation
        listener.onConnection = { [weak self] connection in
            guard let self, self.generation == epoch, self.sessions.count < 4,
                  let invitation = self.invitation, invitation.expiry > Date(), invitation.attempts < 8 else { connection.cancel(); return }
            self.invitation?.attempts += 1
            self.accept(connection, code: invitation.code, epoch: epoch)
        }
        listener.onReady = { [weak self] in
            guard let self, self.generation == epoch else { return }
            self.onReady()
        }
        listener.onFailure = { [weak self] in
            guard let self, self.generation == epoch else { return }
            self.stop(); self.onStatus("配对服务不可用，请重新打开轻剪")
        }
        try listener.start(using: parameters, port: loopbackOnly ? nil : NWEndpoint.Port(rawValue: port)!)
    }
    func stop() {
        generation += 1; closeInvitation(); listener?.stop(); listener = nil
        joining?.finish(); joining = nil
        let existing = Array(sessions.values); sessions.removeAll(); existing.forEach { $0.finish() }
        group = nil
    }
    func openInvitation() throws -> String {
        guard listener != nil, group != nil else { throw ClipError.invalid }
        let code = try makeCode()
        invitation = (code, Date(timeIntervalSinceNow: invitationLifetime), 0)
        return code
    }
    func closeInvitation() { invitation = nil; onInvitationClosed() }
    private func accept(_ connection: NWConnection, code: String, epoch: Int) {
        let id = UUID(), session = Session(connection)
        sessions[id] = session
        session.onFinish = { [weak self] in self?.sessions.removeValue(forKey: id) }
        session.start { [weak self, weak session] in
            guard let self, let session else { return }
            PairMessage.read(session) { [weak self, weak session] hello in
                guard let self, let session, self.generation == epoch, let group = self.group,
                      let current = self.invitation, current.code == code, current.expiry > Date(),
                      hello.type == "pair-hello", let client = hello.deviceId.flatMap(UUID.init(uuidString:)), client != self.localID,
                      hello.deviceId == client.uuidString.lowercased(),
                      hello.groupId == group.id.uuidString.lowercased(), let name = hello.name, validDeviceName(name),
                      let a = hello.spake.flatMap({ Data(base64Encoded: $0) }), a.count == 33 else { throw ClipError.invalid }
                let context = try SpakeContext(role: 1, code: code, client: client, host: self.localID, group: group.id)
                let b = context.message, secret = try context.finish(a), salt = try PairCrypto.random(16)
                let transcript = try PairCrypto.transcript(client: client, host: self.localID, group: group.id, a: a, b: b, salt: salt)
                let key = PairCrypto.derive(secret, salt: salt, transcript: transcript)
                let challenge = PairMessage(type: "pair-challenge", deviceId: self.localID.uuidString.lowercased(), groupId: group.id.uuidString.lowercased(), spake: b.base64EncodedString(), salt: salt.base64EncodedString(), proof: PairCrypto.proof(key, label: "server-confirm", transcript: transcript).base64EncodedString())
                session.send(try challenge.bytes()) { [weak self, weak session] in
                    guard let self, let session else { return }
                    PairMessage.read(session) { [weak self, weak session] response in
                        guard let self, let session, self.generation == epoch,
                              let current = self.invitation, current.code == code, current.expiry > Date(),
                              response.type == "pair-proof", let proof = response.proof.flatMap({ Data(base64Encoded: $0) }),
                              PairCrypto.verify(proof, key: key, label: "client-confirm", transcript: transcript) else { throw ClipError.authentication }
                        let result = PairMessage(type: "pair-result", sealed: try PairCrypto.wrap(group, key: key, transcript: transcript).base64EncodedString())
                        self.closeInvitation()
                        session.send(try result.bytes()) { [weak self, weak session] in
                            guard let self, let session else { return }
                            PairMessage.read(session) { done in
                                guard done.type == "pair-done", let proof = done.proof.flatMap({ Data(base64Encoded: $0) }),
                                      PairCrypto.verify(proof, key: key, label: "stored", transcript: transcript) else { throw ClipError.authentication }
                                self.onStatus("设备已加入群组"); session.finish()
                            }
                        }
                    }
                }
            }
        }
    }
    func join(device: NearbyDevice, code: String, deviceID: UUID, name: String, port: UInt16 = PairingService.port,
              persist: @escaping (SyncGroup) throws -> Void, completion: @escaping (Bool) -> Void) {
        joining?.finish(); joining = nil
        guard let groupID = device.groupID else { onStatus("请让这台设备先创建群组"); completion(false); return }
        do {
            let context = try SpakeContext(role: 0, code: code, client: deviceID, host: device.id, group: groupID)
            let session = Session(NWConnection(host: NWEndpoint.Host(device.host), port: NWEndpoint.Port(rawValue: port)!, using: .tcp))
            joining = session
            var completed = false
            session.onFinish = { [weak self, weak session] in
                guard let self, self.joining === session else { return }
                self.joining = nil
                if completed { self.onStatus("已加入群组，正在自动连接"); completion(true) }
                else { self.onStatus("配对未完成：请核对配对码、有效期和网络"); completion(false) }
            }
            let hello = PairMessage(type: "pair-hello", deviceId: deviceID.uuidString.lowercased(), name: name, groupId: groupID.uuidString.lowercased(), spake: context.message.base64EncodedString())
            onStatus("正在配对…")
            session.start { [weak session] in
                guard let session else { return }
                do {
                    session.send(try hello.bytes()) { [weak session] in
                        guard let session else { return }
                        PairMessage.read(session) { [weak session] challenge in
                            guard let session else { return }
                            guard challenge.type == "pair-challenge", challenge.deviceId == device.id.uuidString.lowercased(),
                                  challenge.groupId == groupID.uuidString.lowercased(),
                                  let b = challenge.spake.flatMap({ Data(base64Encoded: $0) }), b.count == 33,
                                  let salt = challenge.salt.flatMap({ Data(base64Encoded: $0) }), salt.count == 16,
                                  let proof = challenge.proof.flatMap({ Data(base64Encoded: $0) }) else { throw ClipError.invalid }
                            let secret = try context.finish(b)
                            let transcript = try PairCrypto.transcript(client: deviceID, host: device.id, group: groupID, a: context.message, b: b, salt: salt)
                            let key = PairCrypto.derive(secret, salt: salt, transcript: transcript)
                            guard PairCrypto.verify(proof, key: key, label: "server-confirm", transcript: transcript) else { throw ClipError.authentication }
                            let response = PairMessage(type: "pair-proof", proof: PairCrypto.proof(key, label: "client-confirm", transcript: transcript).base64EncodedString())
                            session.send(try response.bytes()) { [weak session] in
                                guard let session else { return }
                                PairMessage.read(session) { [weak session] result in
                                    guard let session else { return }
                                    guard result.type == "pair-result", let sealed = result.sealed.flatMap({ Data(base64Encoded: $0) }) else { throw ClipError.invalid }
                                    let group = try PairCrypto.unwrap(sealed, expected: groupID, key: key, transcript: transcript)
                                    try persist(group)
                                    completed = true
                                    let done = PairMessage(type: "pair-done", proof: PairCrypto.proof(key, label: "stored", transcript: transcript).base64EncodedString())
                                    session.send(try done.bytes()) { session.finish() }
                                }
                            }
                        }
                    }
                } catch { session.finish() }
            }
        } catch { onStatus("请输入 8 位数字配对码"); completion(false) }
    }
}
