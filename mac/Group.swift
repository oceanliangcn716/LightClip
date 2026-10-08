import Foundation
import Security

func validDeviceName(_ name: String) -> Bool {
    !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && name.utf8.count <= 96 &&
    !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
}

struct DeviceProfile {
    let id: UUID
    var name: String
    static func friendlyName() -> String {
        let adjectives = ["甜甜的", "软软的", "开心的", "慢悠悠的", "圆滚滚的", "暖暖的", "会飞的", "闪亮的", "安静的", "元气满满的", "爱笑的", "懒洋洋的"]
        let fruits = ["香蕉", "苹果", "桃子", "橘子", "草莓", "菠萝", "芒果", "西瓜", "葡萄", "樱桃", "柠檬", "椰子"]
        return adjectives.randomElement()! + fruits.randomElement()!
    }
    static func load(_ defaults: UserDefaults = .standard) -> DeviceProfile {
        let id = defaults.string(forKey: "deviceID").flatMap(UUID.init(uuidString:)) ?? UUID()
        let saved = defaults.string(forKey: "deviceName")
        let name = saved.flatMap { validDeviceName($0) ? $0 : nil } ?? friendlyName()
        defaults.set(id.uuidString.lowercased(), forKey: "deviceID")
        defaults.set(name, forKey: "deviceName")
        return DeviceProfile(id: id, name: name)
    }
}

enum GroupStore {
    static func load() -> SyncGroup? {
        let defaults = UserDefaults.standard
        guard let key = KeyStore.load(), key.count == 32 else { return nil }
        if let id = defaults.string(forKey: "groupID").flatMap(UUID.init(uuidString:)) { return SyncGroup(id: id, key: key) }
        // Preserve the established pairing secret while migrating the old fixed-IP setup.
        guard defaults.bool(forKey: "enabled"), defaults.string(forKey: "peer") != nil else { return nil }
        let id = UUID(); defaults.set(id.uuidString.lowercased(), forKey: "groupID")
        defaults.set(false, forKey: "groupPaused")
        return SyncGroup(id: id, key: key)
    }
    static func save(_ group: SyncGroup) throws {
        guard group.key.count == 32 else { throw ClipError.invalid }
        try KeyStore.save(group.key)
        let defaults = UserDefaults.standard
        defaults.set(group.id.uuidString.lowercased(), forKey: "groupID")
        defaults.set(false, forKey: "groupPaused")
        defaults.set(true, forKey: "enabled")
    }
    static func leave() throws {
        try KeyStore.delete()
        let defaults = UserDefaults.standard
        for field in ["groupID", "groupPaused", "peer", "enabled"] { defaults.removeObject(forKey: field) }
    }
}
