import AppKit
import Security
import CryptoKit
import ServiceManagement
import Darwin

enum KeyStore {
    static let service = "cn.oceanliang.lightclip.pairing"
    static let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: "peer"]
    private static var sessionKey: Data?
    private(set) static var lastLoadStatus: OSStatus = errSecSuccess
    static func load() -> Data? {
        // A single approved read is enough for this app session. Opening settings
        // must not repeat a system prompt after the user selected Allow Once.
        if let sessionKey { return sessionKey }
        var q = query; q[kSecReturnData as String] = true; q[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        lastLoadStatus = SecItemCopyMatching(q as CFDictionary, &result)
        guard lastLoadStatus == errSecSuccess else { return nil }
        guard let data = result as? Data else { return nil }
        sessionKey = data
        return data
    }
    static func save(_ data: Data) throws {
        let update = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if update == errSecSuccess { sessionKey = data; return }
        guard update == errSecItemNotFound else { throw ClipError.authentication }
        var q = query; q[kSecValueData as String] = data
        q[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        guard SecItemAdd(q as CFDictionary, nil) == errSecSuccess else { throw ClipError.authentication }
        sessionKey = data
    }
    static func delete() throws {
        let result = SecItemDelete(query as CFDictionary)
        guard result == errSecSuccess || result == errSecItemNotFound else { throw ClipError.authentication }
        sessionKey = nil
    }
}

func localAddresses() -> String {
    var list: UnsafeMutablePointer<ifaddrs>?
    guard getifaddrs(&list) == 0 else { return "请在网络设置中查看" }
    defer { freeifaddrs(list) }
    var values: [String] = []
    var cursor = list
    while let current = cursor {
        defer { cursor = current.pointee.ifa_next }
        let entry = current.pointee
        guard let addr = entry.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET),
              entry.ifa_flags & UInt32(IFF_LOOPBACK) == 0 else { continue }
        var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
        if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &buffer, socklen_t(buffer.count), nil, 0, NI_NUMERICHOST) == 0 {
            values.append("\(String(cString: entry.ifa_name)): \(String(cString: buffer))")
        }
    }
    return values.joined(separator: "   ")
}

func numericAddress(_ text: String) -> Bool {
    var v4 = in_addr(), v6 = in6_addr()
    return inet_pton(AF_INET, text, &v4) == 1 || inet_pton(AF_INET6, text, &v6) == 1
}

final class App: NSObject, NSApplicationDelegate, NSTableViewDataSource, NSTableViewDelegate {
    private let engine = SyncEngine()
    private let board = ClipBoard()
    private let streams = StreamEngine()
    private let discovery = LANDiscovery()
    private let pairing = PairingService()
    private var profile = DeviceProfile.load()
    private var group: SyncGroup?
    private var nearby: [NearbyDevice] = []
    private var loadingPairing = true
    private var selectedDeviceID: UUID?
    private var verified: [String: Date] = [:]
    private var healthTimer: Timer?
    private var invitationExpiry: DispatchWorkItem?
    private var wakeObserver: NSObjectProtocol?
    private var item: NSStatusItem!
    private var menuStatus: NSMenuItem!
    private var menuReceived: NSMenuItem!
    private var menuPause: NSMenuItem!
    private var window: NSWindow!
    private let nameField = NSTextField()
    private let groupLabel = NSTextField(labelWithString: "尚未加入群组")
    private let statusLabel = NSTextField(labelWithString: "正在启动…")
    private let receiveLabel = NSTextField(labelWithString: "最近接收：本次启动尚无记录")
    private let inviteLabel = NSTextField(labelWithString: "添加设备时，在这里生成配对码")
    private let table = NSTableView()
    private let codeField = NSSecureTextField()
    private let loginCheck = NSButton(checkboxWithTitle: "开机自动连接群组", target: nil, action: nil)
    private var createButton: NSButton!
    private var inviteButton: NSButton!
    private var joinButton: NSButton!
    private var leaveButton: NSButton!
    private var pauseButton: NSButton!
    private var paused: Bool { UserDefaults.standard.bool(forKey: "groupPaused") }

    func applicationDidFinishLaunching(_ note: Notification) {
        NSApp.setActivationPolicy(.regular)
        buildMenu(); buildWindow()
        board.onStatus = { [weak self] in self?.status($0) }
        board.onLocalChange = { [weak self] in self?.engine.cancelSending(); self?.streams.cancelTransfers() }
        board.onFiles = { [weak self] in self?.streams.sendFiles($0) }
        board.onLargeText = { [weak self] in self?.streams.sendText($0) }
        board.onChange = { [weak self] in self?.engine.send($0) }
        streams.onStatus = { [weak self] in self?.status($0) }
        streams.onBegin = { [weak self] in self?.board.changeVersion ?? 0 }
        streams.onReceive = { [weak self] received, version in
            guard let self else { throw ClipError.invalid }
            try self.board.apply(received, expectedVersion: version)
            self.engine.cancelSending(); self.streams.cancelSending()
            self.recordReceived(received.offer.type == "text" ? "文字" : "文件")
        }
        streams.onPeerState = { [weak self] host, connected in
            guard let self else { return }
            if connected { self.verified[host] = Date() }
            self.refreshUI()
        }
        engine.onStatus = { [weak self] value in
            guard let self else { return }
            if !self.engine.active { self.board.stop(); self.streams.stop() }
            self.status(value)
        }
        engine.onReceive = { [weak self] packet in
            guard let self else { throw ClipError.invalid }
            if packet.kind != 0 { self.streams.cancelTransfers() }
            try self.board.apply(packet)
            if packet.kind == 1 || packet.kind == 2 {
                self.recordReceived(packet.kind == 1 ? "文字" : "图片")
            }
        }
        engine.onPeerState = { [weak self] host, connected in
            guard let self else { return }
            if connected { self.verified[host] = Date() } else { self.verified.removeValue(forKey: host) }
            self.refreshUI()
        }
        discovery.onUpdate = { [weak self] devices in
            guard let self else { return }
            let oldHosts = Set(self.members.map(\.host))
            self.nearby = devices
            let hosts = Set(self.members.map(\.host))
            self.verified = self.verified.filter { hosts.contains($0.key) }
            self.engine.updatePeers(Array(hosts))
            self.streams.updatePeers(Array(hosts))
            for host in hosts.subtracting(oldHosts) { self.engine.probe(host) }
            self.refreshUI()
        }
        pairing.onStatus = { [weak self] in self?.status($0) }
        pairing.onInvitationClosed = { [weak self] in
            self?.invitationExpiry?.cancel(); self?.invitationExpiry = nil
            self?.inviteLabel.stringValue = "添加设备时，在这里生成配对码"
        }
        do { try discovery.start(deviceID: profile.id, name: profile.name, groupID: nil) }
        catch { status("局域网发现不可用，请检查轻剪的本地网络权限") }
        // Keychain may wait for a system approval. Keep the window responsive
        // while the OS performs that read, without weakening its access checks.
        status("正在读取已保存的群组…")
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let saved = GroupStore.load()
            DispatchQueue.main.async {
                guard let self else { return }
                self.loadingPairing = false; self.group = saved
                if saved != nil { self.activateGroup(); self.configureAutoStart() }
                else if self.needsKeychainAccess { self.status("原有配对尚未读取；请点“重新读取配对”并完成系统授权") }
                else { self.status("先创建群组，或选择下方设备输入配对码加入") }
                self.refreshUI()
            }
        }
        healthTimer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.verified = self.verified.filter { Date().timeIntervalSince($0.value) < 65 }
            for device in self.members { self.engine.probe(device.host) }
            self.refreshUI()
        }
        healthTimer?.tolerance = 5
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            self?.discovery.announceNow()
            if self?.group != nil { self?.activateGroup() }
        }
        refreshUI(); showWindow()
    }
    private var members: [NearbyDevice] { guard let group else { return [] }; return nearby.filter { $0.groupID == group.id } }
    private func recordReceived(_ kind: String) {
        let formatter = DateFormatter(); formatter.dateFormat = "HH:mm:ss"
        let text = "最近接收：\(kind) · \(formatter.string(from: Date()))"
        receiveLabel.stringValue = text; menuReceived.title = text
    }
    private var needsKeychainAccess: Bool {
        let saved = UserDefaults.standard.string(forKey: "groupID") != nil ||
            (UserDefaults.standard.bool(forKey: "enabled") && UserDefaults.standard.string(forKey: "peer") != nil)
        return group == nil && saved && KeyStore.lastLoadStatus != errSecSuccess && KeyStore.lastLoadStatus != errSecItemNotFound
    }
    private func status(_ text: String) { statusLabel.stringValue = text; menuStatus?.title = text; item?.button?.toolTip = "轻剪：\(text)" }
    private func activateGroup() {
        guard let group else { return }
        board.stop(); engine.stop(); streams.stop(); verified.removeAll()
        do {
            try pairing.start(deviceID: profile.id, name: profile.name, group: group)
            discovery.update(name: profile.name, groupID: group.id)
            if paused { status("已暂停同步；群组信息仍然保留") }
            else {
                try engine.start(host: "", key: group.key)
                try streams.start(key: group.key)
                engine.updatePeers(members.map(\.host)); streams.updatePeers(members.map(\.host)); board.start()
                for device in members { engine.probe(device.host) }
                status("群组已保存，等待其他设备上线")
            }
        } catch { board.stop(); engine.stop(); streams.stop(); status("群组服务暂时不可用，请重新打开轻剪") }
        refreshUI()
    }
    private func configureAutoStart() {
        guard !UserDefaults.standard.bool(forKey: "groupAutostartConfigured") else { refreshLogin(); return }
        do {
            if SMAppService.mainApp.status == .notRegistered { try SMAppService.mainApp.register() }
            UserDefaults.standard.set(true, forKey: "groupAutostartConfigured")
            if SMAppService.mainApp.status == .requiresApproval { status("请在系统登录项中允许轻剪开机启动") }
        } catch { status("群组已保存；开机启动尚未获系统允许") }
        refreshLogin()
    }
    private func refreshLogin() {
        let state = SMAppService.mainApp.status
        loginCheck.state = state == .enabled ? .on : (state == .requiresApproval ? .mixed : .off)
        loginCheck.title = state == .requiresApproval ? "开机自动连接群组（需系统允许）" : "开机自动连接群组"
    }
    @objc private func changeLogin() {
        do {
            if loginCheck.state != .off, SMAppService.mainApp.status == .requiresApproval { SMAppService.openSystemSettingsLoginItems() }
            else if loginCheck.state == .on, SMAppService.mainApp.status == .notRegistered { try SMAppService.mainApp.register() }
            else if loginCheck.state == .off, SMAppService.mainApp.status != .notRegistered { try SMAppService.mainApp.unregister() }
            UserDefaults.standard.set(true, forKey: "groupAutostartConfigured")
        } catch { status("开机启动设置未完成，请检查系统登录项") }
        refreshLogin()
    }
    @objc private func saveName() {
        let value = nameField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard validDeviceName(value) else { status("请输入简短、有效的设备名字"); return }
        profile.name = value; UserDefaults.standard.set(value, forKey: "deviceName")
        discovery.update(name: value, groupID: group?.id); status("设备名字已保存")
    }
    @objc private func createGroup() {
        guard group == nil else { return }
        if needsKeychainAccess {
            group = GroupStore.load()
            if group != nil { activateGroup(); configureAutoStart() }
            else { status("原有配对未读取，请在系统弹窗允许轻剪访问钥匙串后重试") }
            refreshUI(); return
        }
        do {
            let new = SyncGroup(id: UUID(), key: try PairCrypto.random(32))
            try GroupStore.save(new); group = new
            activateGroup(); configureAutoStart(); inviteDevice()
        } catch { status("群组未创建：无法保存安全配对信息") }
    }
    @objc private func inviteDevice() {
        do {
            let code = try pairing.openInvitation()
            inviteLabel.stringValue = code.prefix(4) + " " + code.suffix(4)
            invitationExpiry?.cancel()
            let work = DispatchWorkItem { [weak self] in self?.pairing.closeInvitation() }
            invitationExpiry = work; DispatchQueue.main.asyncAfter(deadline: .now() + 120, execute: work)
            status("在新设备选择“\(profile.name)”，输入上方配对码；2 分钟内有效")
        } catch { status("请先创建或加入群组") }
    }
    @objc private func joinGroup() {
        guard group == nil else { status("已在群组内；如需更换，请先退出当前群组"); return }
        guard nearby.indices.contains(table.selectedRow) else { status("请先选择一台已经创建群组的设备"); return }
        let device = nearby[table.selectedRow]
        let code = codeField.stringValue.filter { !$0.isWhitespace }
        joinButton.isEnabled = false
        pairing.join(device: device, code: code, deviceID: profile.id, name: profile.name, persist: { [weak self] new in
            try GroupStore.save(new); self?.group = new
        }, completion: { [weak self] succeeded in
            guard let self else { return }
            self.codeField.stringValue = ""
            if succeeded { self.activateGroup(); self.configureAutoStart() }
            self.refreshUI()
        })
    }
    @objc private func leaveGroup() {
        guard group != nil else { return }
        let alert = NSAlert(); alert.messageText = "退出当前同步群组？"
        alert.informativeText = "本机会停止同步并忘记配对。以后重新加入需要新的配对码。其他设备仍可继续同步。"
        alert.addButton(withTitle: "退出群组"); alert.addButton(withTitle: "取消")
        alert.beginSheetModal(for: window) { [weak self] response in
            guard let self, response == .alertFirstButtonReturn else { return }
            do {
                try GroupStore.leave()
                self.board.stop(); self.engine.stop(); self.streams.stop(); self.pairing.stop(); self.group = nil; self.verified.removeAll()
                self.discovery.update(name: self.profile.name, groupID: nil)
                if SMAppService.mainApp.status != .notRegistered { try? SMAppService.mainApp.unregister() }
                UserDefaults.standard.removeObject(forKey: "groupAutostartConfigured")
                self.status("已退出群组"); self.refreshLogin(); self.refreshUI()
            } catch { self.status("未能清除配对信息，群组仍然保留") }
        }
    }
    @objc private func togglePause() {
        guard group != nil else { return }
        UserDefaults.standard.set(!paused, forKey: "groupPaused"); activateGroup()
    }
    @objc private func ping() { if engine.active { engine.send(Packet(kind: 0, body: Data())) } }
    @objc private func cancelTransfer() { engine.cancelSending(); streams.cancelTransfers(); status("已取消当前传输，剪贴板保持原样") }
    @objc private func openReceivedFiles() {
        let url = streams.storage.root
        do { try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700]); NSWorkspace.shared.open(url) }
        catch { status("无法打开接收文件夹") }
    }
    @objc private func sendCurrent() { if engine.active { board.capture() } else { status("请先启用群组同步") } }
    @objc private func showWindow() { window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showWindow(); return true }
    @objc private func quit() { NSApp.terminate(nil) }
    func applicationWillTerminate(_ note: Notification) {
        healthTimer?.invalidate(); invitationExpiry?.cancel(); board.stop(); engine.stop(); streams.stop(); pairing.stop(); discovery.stop()
        streams.storage.queue.sync {}
        if let wakeObserver { NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver) }
    }
    private func refreshUI() {
        guard window != nil else { return }
        let connected = Set(members.filter { verified[$0.host] != nil }.map(\.id)).count
        groupLabel.stringValue = group == nil ? "尚未加入群组" : "我的同步群组 · 已连接 \(connected) 台其他设备"
        createButton.isEnabled = group == nil && !loadingPairing; createButton.title = needsKeychainAccess ? "重新读取配对" : "创建同步群组"
        inviteButton.isEnabled = group != nil; leaveButton.isEnabled = group != nil
        pauseButton.isEnabled = group != nil; pauseButton.title = paused ? "恢复同步" : "暂停同步"
        menuPause.title = pauseButton.title; menuPause.isEnabled = group != nil
        loginCheck.isEnabled = group != nil
        joinButton.isEnabled = group == nil && !loadingPairing && !needsKeychainAccess && nearby.indices.contains(table.selectedRow) && nearby[table.selectedRow].groupID != nil
        codeField.isEnabled = group == nil && !loadingPairing && !needsKeychainAccess
        let selected = selectedDeviceID
        table.reloadData()
        if let selected, let index = nearby.firstIndex(where: { $0.id == selected }) { table.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false) }
        else { table.deselectAll(nil) }
    }
    func numberOfRows(in tableView: NSTableView) -> Int { nearby.count }
    func tableViewSelectionDidChange(_ notification: Notification) {
        selectedDeviceID = nearby.indices.contains(table.selectedRow) ? nearby[table.selectedRow].id : nil
        joinButton.isEnabled = group == nil && !loadingPairing && !needsKeychainAccess && nearby.indices.contains(table.selectedRow) && nearby[table.selectedRow].groupID != nil
    }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard nearby.indices.contains(row) else { return nil }
        let device = nearby[row]
        let text: String
        if tableColumn?.identifier.rawValue == "name" { text = device.name }
        else if let group, device.groupID == group.id { text = verified[device.host] == nil ? (paused ? "同组 · 已暂停" : "同组 · 正在连接") : "同组 · 已连接" }
        else { text = device.groupID == nil ? "尚未建群组" : "可加入的群组" }
        let label = NSTextField(labelWithString: text); label.lineBreakMode = .byTruncatingTail
        return label
    }
    @discardableResult private func add(_ menu: NSMenu, _ title: String, _ action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: ""); item.target = self; menu.addItem(item); return item
    }
    private func buildMenu() {
        let main = NSMenu(), appMenu = NSMenu()
        add(appMenu, "打开轻剪", #selector(showWindow)); appMenu.addItem(.separator())
        add(appMenu, "退出轻剪", #selector(quit)).keyEquivalent = "q"
        let appEntry = NSMenuItem(); appEntry.submenu = appMenu; main.addItem(appEntry)
        let edit = NSMenu(title: "编辑")
        for (title, selector, key) in [("撤销", "undo:", "z"), ("剪切", "cut:", "x"), ("复制", "copy:", "c"), ("粘贴", "paste:", "v"), ("全选", "selectAll:", "a")] { edit.addItem(NSMenuItem(title: title, action: NSSelectorFromString(selector), keyEquivalent: key)) }
        let editEntry = NSMenuItem(title: "编辑", action: nil, keyEquivalent: ""); editEntry.submenu = edit; main.addItem(editEntry); NSApp.mainMenu = main
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = NSImage(systemSymbolName: "doc.on.clipboard", accessibilityDescription: "轻剪")
        let menu = NSMenu(); menu.addItem(NSMenuItem(title: "轻剪 · 同步群组", action: nil, keyEquivalent: ""))
        menuStatus = NSMenuItem(title: "正在启动…", action: nil, keyEquivalent: ""); menu.addItem(menuStatus)
        menuReceived = NSMenuItem(title: receiveLabel.stringValue, action: nil, keyEquivalent: ""); menu.addItem(menuReceived)
        menu.addItem(.separator()); add(menu, "打开轻剪…", #selector(showWindow))
        menuPause = add(menu, "暂停同步", #selector(togglePause))
        add(menu, "测试群组连接", #selector(ping)); add(menu, "发送当前剪贴板", #selector(sendCurrent))
        add(menu, "取消当前传输", #selector(cancelTransfer)); add(menu, "打开接收文件夹…", #selector(openReceivedFiles))
        menu.addItem(.separator()); add(menu, "退出轻剪", #selector(quit)); item.menu = menu
    }
    private func label(_ text: String, x: CGFloat = 28, y: CGFloat, width: CGFloat = 504, height: CGFloat = 22) -> NSTextField {
        let field = NSTextField(wrappingLabelWithString: text); field.frame = NSRect(x: x, y: y, width: width, height: height); window.contentView?.addSubview(field); return field
    }
    private func button(_ title: String, x: CGFloat, y: CGFloat, width: CGFloat, action: Selector) -> NSButton {
        let button = NSButton(title: title, target: self, action: action); button.frame = NSRect(x: x, y: y, width: width, height: 32); window.contentView?.addSubview(button); return button
    }
    private func buildWindow() {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 680), styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "轻剪 · 局域网同步群组"; window.isReleasedWhenClosed = false; window.center()
        label("让剪贴板在你的设备间流动", y: 626, height: 34).font = .systemFont(ofSize: 23, weight: .semibold)
        _ = label("这台设备的名字", y: 594)
        nameField.stringValue = profile.name; nameField.frame = NSRect(x: 28, y: 564, width: 386, height: 26); nameField.target = self; nameField.action = #selector(saveName); window.contentView?.addSubview(nameField)
        _ = button("保存名字", x: 428, y: 561, width: 104, action: #selector(saveName))
        groupLabel.frame = NSRect(x: 28, y: 526, width: 504, height: 25); groupLabel.font = .systemFont(ofSize: 16, weight: .semibold); window.contentView?.addSubview(groupLabel)
        statusLabel.frame = NSRect(x: 28, y: 489, width: 504, height: 25); statusLabel.lineBreakMode = .byTruncatingTail; statusLabel.font = .systemFont(ofSize: 12); window.contentView?.addSubview(statusLabel)
        receiveLabel.frame = NSRect(x: 28, y: 466, width: 390, height: 20); receiveLabel.font = .systemFont(ofSize: 12); receiveLabel.textColor = .secondaryLabelColor; window.contentView?.addSubview(receiveLabel)
        _ = button("取消传输", x: 428, y: 459, width: 104, action: #selector(cancelTransfer))
        createButton = button("创建同步群组", x: 28, y: 425, width: 150, action: #selector(createGroup))
        inviteButton = button("生成配对码", x: 188, y: 425, width: 150, action: #selector(inviteDevice))
        leaveButton = button("退出群组", x: 388, y: 425, width: 144, action: #selector(leaveGroup))
        inviteLabel.frame = NSRect(x: 28, y: 389, width: 504, height: 28); inviteLabel.font = .monospacedDigitSystemFont(ofSize: 21, weight: .medium); window.contentView?.addSubview(inviteLabel)
        label("配对码 2 分钟内有效，仅可使用一次；不需要手填 IP。", y: 367).textColor = .secondaryLabelColor
        _ = label("附近的设备", y: 337)
        let name = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("name")); name.title = "设备名字"; name.width = 290; table.addTableColumn(name)
        let state = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("state")); state.title = "状态"; state.width = 190; table.addTableColumn(state)
        table.delegate = self; table.dataSource = self; table.rowHeight = 28; table.allowsMultipleSelection = false
        let scroll = NSScrollView(frame: NSRect(x: 28, y: 171, width: 504, height: 156)); scroll.documentView = table; scroll.hasVerticalScroller = true; scroll.borderType = .bezelBorder; window.contentView?.addSubview(scroll)
        _ = label("加入群组：选择上方设备，输入它显示的 8 位配对码", y: 140)
        codeField.frame = NSRect(x: 28, y: 109, width: 205, height: 26); codeField.placeholderString = "8 位配对码"; window.contentView?.addSubview(codeField)
        joinButton = button("加入选中设备的群组", x: 247, y: 105, width: 285, action: #selector(joinGroup))
        pauseButton = button("暂停同步", x: 28, y: 65, width: 150, action: #selector(togglePause))
        loginCheck.frame = NSRect(x: 205, y: 69, width: 327, height: 25); loginCheck.target = self; loginCheck.action = #selector(changeLogin); window.contentView?.addSubview(loginCheck)
        let hint = label("文字上限 10 MiB · 文件单批上限 5 GiB · 同组自动重连\n接收文件保存在本机缓存；可从菜单打开接收文件夹整理。", y: 17, height: 39)
        hint.font = .systemFont(ofSize: 11); hint.textColor = .secondaryLabelColor
        refreshLogin()
    }
}
