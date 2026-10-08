# LightClip 0.3.1 / build 5

本版本提供 Mac 顶部菜单栏常驻客户端和跨平台协议。最新仓库已导入 PC 提供的 Windows 0.3.0 完整群组源码；生产入口为 `windows/LightClip.Group/LightClip.Group.csproj`。

Windows x64 自包含运行包已收到，外层 52 个校验值及内部 ZIP 完整性通过；native 配对 DLL 中仍有三条 Rust 编译个人目录路径，所以原 Windows 运行包没有上传。已补上构建路径映射和发布前检查，待 PC 重新构建及验证后再提供 Windows EXE。当前 release 中的旧 Windows handoff ZIP 仅为历史开发者参考资料。详情见最新仓库的 `WINDOWS_IMPORT_REVIEW.md`。

- Mac 采用 accessory / `LSUIElement` 菜单栏常驻模式，不显示 Dock 图标；已有群组启动时不自动弹出设置窗口。
- 首次使用或读取已保存配对失败时显示设置窗口；设置窗口关闭后同步继续，用户可以从菜单栏重新打开设置。
- 0.3.0 的文件与大文字流式协议、剪贴板边界、取消清理、缓存预算和跨语言夹具保持不变。

- 局域网设备友好命名、自动发现、一次性 8 位配对码、多设备群组及保存配对后的自动连接。
- 文字最多 10 MiB UTF-8；普通文件最多 32 个，单个及整批最多 5 GiB。
- 文件按 256 KiB 分块加密传输，每块确认，完成后校验 SHA-256 才更新剪贴板。
- 文件 URL/FileDrop 粘贴，符合上限的单 PNG/JPG 同时提供图片内容。
- 取消传输、保护新复制内容、失败和异常退出后临时文件清理、接收文件夹入口及 10 GiB 缓存预算。
- 修正 Mac 构建产物与最低系统标注不一致的问题，显式使用 macOS 14.0 部署目标。

Mac 下载包为 Apple Silicon/arm64，临时签名、未公证；macOS 14 和 Intel 真机尚未验证。当前在开发机完成加密、群组、文件流、跨语言夹具及完整 5 GiB 回环测试。开发机正常窗口 footprint 约 28 MB；独立 5 GiB 发送/接收测试最大 RSS 约 16.6 MiB，两者为不同测量场景。

用户报告已升级的真实 PC 与 Mac 双向文件粘贴可用。真实 Windows 5 GiB 传输、Windows 内存、重启恢复、DHCP 地址变化、多 PC 和 CrossPaste 暂停负对照尚未由本仓库完整验收；详见 tests/verification.json 和 tests/双机隔离验收.md。

接收完成的文件保留在应用缓存以供粘贴，会占用磁盘空间。文字暂存完成后删除。项目不提供离线补发、断点续传、目录传输、云端中继或剪贴板历史列表。

第三方许可声明见 THIRD_PARTY_NOTICES.txt。当前未选择覆盖整个项目源码的顶层开源许可证；请勿把仓库可见性视为另行授予的许可。
