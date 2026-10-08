# 轻剪 · LightClip 0.3.1 / build 5

轻剪是一个只在局域网工作的文字、图片和普通文件双向剪贴板同步工具。0.3.1 / build 5 保留 0.3.0 的 `LightClip/v1` 兼容限制和 `STREAM_V2` 文件/大文字流式传输：严格 UTF-8 文字最多 10 MiB，单次最多 32 个普通文件，单文件和整批总量均最多 5 GiB。文件按 256 KiB 块读取或写入，每块写盘、更新 SHA-256 并收到 ACK 后才继续；接收完成前不改剪贴板。没有云端中继、剪贴板历史或离线内容队列。

协议分工固定为：UDP `49286` 负责未认证的局域网发现，TCP `49288` 负责一次性短码 PAKE 配对，TCP `49287` 继续承载 `LightClip/v1` 的文字/PNG/ACK，TCP `49289` 承载 `LightClip/stream/v2` 的长文字和普通文件。完整的新增流式字节布局、manifest、超时、缓存、取消和验收边界见 [`STREAM_V2.md`](STREAM_V2.md)；旧内容协议仍以 [`PROTOCOL.md`](PROTOCOL.md) 为准。

## 当前仓库包含什么

| 部分 | 状态 |
|---|---|
| Mac 客户端 | 0.3.1 / build 5 菜单栏应用，群组配对、文字/图片/文件同步 |
| Windows 共享组件 | 内容协议、配对桥、文件流及跨语言测试 |
| Windows UI 参考实现 | 可编译的一对一 WinForms 参考版；不是 PC 上已修改的最终群组客户端 |

用户已反馈在自己的 Mac 与升级后的 PC 之间双向文件传输可用。这个反馈尚未替代项目的完整验收矩阵；PC 上最终客户端源码尚未合并到本仓库。仓库中的 Windows 参考实现和交接提示词用于合并对接，不能直接替换已完成群组功能的 PC 客户端。

## 从源码构建

Mac 构建需要 Xcode Command Line Tools 和 Rust 工具链。在仓库根目录运行：

```sh
zsh build-mac.sh
```

产物是 `dist/轻剪.app`，按构建机器生成 arm64 或 x86_64 程序；当前提供的 Mac 二进制为 **Apple Silicon / arm64**。Swift 的部署目标、Rust 部署环境及应用最低系统标注统一为 macOS 14.0；当前运行测试在开发机完成，macOS 14 真机和 Intel Mac 尚未运行验证。构建结果为临时签名，不包含 Apple 公证或发布证书。升级已有本地安装时，macOS 可能要求重新确认钥匙串访问。

Windows 参考源码需要 .NET 10 SDK；native 配对桥还需要官方 Rust MSVC 工具链和对应 Visual C++ 构建工具。`pairing/build-windows.ps1 -Architecture x64` 或 `arm64` 构建匹配架构的 native DLL，再运行 `dotnet build windows/LightClip.Windows.csproj`。这是开发者参考构建，完整群组客户端应合并实际 PC 代码后再发布；本次未提供 Windows 最终 EXE。

本机独立测试不会读取真实剪贴板内容或使用真实群组密钥：

```sh
dist/轻剪.app/Contents/MacOS/LightClip --self-test
dist/轻剪.app/Contents/MacOS/LightClip --stream-self-test
dist/轻剪.app/Contents/MacOS/LightClip --group-self-test
cargo test --locked --manifest-path pairing/Cargo.toml
dotnet run --project tests/StreamInterop.csproj -- native
dotnet run --project tests/StreamInterop.csproj -- text
dotnet run --project tests/StreamInterop.csproj -- negative
```

完整 5 GiB 测试为 `--stream-big-test`，会在临时目录写入约 5 GiB 接收文件，请预留磁盘空间。`server`/`client` 互通夹具需要成对启动；它们的独立端口与生产端口不同。

## Mac 端的最新群组流程

首次启动保持未启用，不读取剪贴板，也不启动内容监听。先在“轻剪 · 局域网同步群组”窗口保存友好设备名，再选择“创建同步群组”。Mac 会保存群组身份，并在局域网 UDP `49286` 公告自己的名字和群组状态。附近列表只用于发现，不承担认证；来源地址取自收到公告的网络地址。

另一台设备在附近列表中选择已建群组的设备，输入对方窗口显示的 8 位配对码即可加入。配对码通过 TCP `49288` 进行一次性 PAKE 配对，有效期 2 分钟，成功一次后失效；不需要手填 IP。一个群组可以继续加入多台设备。群组成员地址由发现公告更新，DHCP 或 Wi‑Fi 造成 IP 变化后会自动重连。群组密钥保存在 Mac 钥匙串，群组 ID 和设备名保存在本机偏好设置。旧配对读取遇到系统授权未完成时，界面提供“重新读取配对”，不会直接覆盖原有配对。

加入群组后，普通小文字和图片继续使用 TCP `49287` 的 `LightClip/v1`。该内容协议、AES-256-GCM 封套、ACK、时间戳、重放保护、文字/PNG 上限保持不变，只是每个群组成员使用同一份群组密钥。大于 1 MiB 的文字和普通文件使用 TCP `49289` 的 `LightClip/stream/v2`，每个群组最多维护 2 个并行出站流和 2 个并行入站流；每次只发送当前剪贴板，不补发暂停或离线期间的旧内容。单张小 PNG/JPG 文件可以保留原文件字节并同时提供图片格式，文字在提交剪贴板后删除临时文字文件，普通文件完成后保留在应用缓存目录供 Finder/Explorer 粘贴。

Mac 客户端仅作为顶部菜单栏常驻的 accessory 应用运行，不显示 Dock 图标。已有群组时启动不会自动弹出设置窗口；首次使用或读取已保存配对失败时才显示设置窗口。窗口关闭后同步继续运行，用户可以从菜单栏重新打开设置。菜单栏和设置窗口提供群组状态、附近设备、暂停/恢复、测试群组连接、发送当前剪贴板、生成配对码和退出群组。暂停会关闭内容监听与连接，但保留群组和重放保护状态，恢复后不会补发暂停期间的内容。明确选择“退出群组”才会停止同步并忘记本机的群组 ID、群组密钥和相关自动启动状态。完成配对后，开机自动连接默认启用；用户可以在设置中关闭。关闭设置窗口或主窗口只隐藏到菜单栏，不会退出同步。

支持文字、emoji、多行文字、截图、透明 PNG，以及普通文件的 FileDrop。1 MiB 以内的文字和 8 MiB 以内的图片继续走 v1；较大文字走 49289，严格 UTF-8 上限为 10 MiB；普通文件一次最多 32 个，单文件和整批总量最多 5 GiB。单张本地 PNG/JPG/JPEG 可以读取图片并同时保留原文件字节，目录、符号链接、reparse point、网络共享路径和不符合跨平台命名要求的文件会跳过；源图片文件读取最多 64 MiB，PNG 最多 1600 万像素。文字提交后删除临时文字文件，普通文件完成后保留在应用缓存中供 Finder/Explorer 粘贴。程序不维护剪贴板历史列表；通过文件复制接收的图片同样会保留在文件缓存中，不把文件路径和密钥写入日志或普通配置文件。

旧版本的手动配对可以由 Mac 保留已有钥匙串密钥并按迁移边界继续使用，但这不等于已经加入 0.2.0 群组。Windows 端要恢复与 Mac 的正常同步，仍需完成 Windows 群组升级。

## 0.3.0 文件流基线与本地验证边界（0.3.1 保留）

0.3.1 / build 5 本轮只调整 Mac 菜单栏常驻和设置窗口的启动行为；下面保留 0.3.0 文件流功能及其验证边界。当前主线已完成一次本机回环的完整 5 GiB 流式传输：`5368709120` 字节、接收 SHA-256 一致、完成 ACK，耗时约 11.23 秒；同进程 sender/receiver 的最大 RSS 约 `17,448,960` 字节（16.6 MiB，属于传输自测进程，不是 GUI 内存测量）。这只是回环和实现级证据，不能替代真实网络吞吐、Windows 真机或两台设备验收。10 MiB 文字边界、空文件、多文件、取消清理及双向 Swift/C# 流式互通已通过本机夹具；真实两机仍应按 `STREAM_V2.md` 和 `tests/双机隔离验收.md` 复核。

## Windows 交接边界

`windows/Program.cs` 仍是已有的一对一 WinForms 客户端草稿，已接入普通 FileDrop、长文字流、取消和接收文件夹入口，但不是已经完成的 Windows 群组 UI。`windows/PairingCrypto.cs` 是可供 Windows Codex 复用的 Rust SPAKE 桥接和配对加密实现；它不表示 Windows 的发现、群组管理、托盘流程或真实剪贴板运行已经完成。Windows 群组升级应以 `PAIRING_V2.md`、`PROTOCOL.md`、`pairing/Cargo.lock`、`pairing/build-windows.ps1` 和测试夹具为准，并合并目标 PC 已有的 DPAPI、图标和剪贴板修复，不能盲目覆盖。

需要交给 PC 上 Codex 时，群组流程使用 [`给PC上Codex的群组升级提示词.md`](给PC上Codex的群组升级提示词.md)，文件和大文字接线使用 [`给PC上Codex的文件升级提示词.md`](给PC上Codex的文件升级提示词.md)。两份提示词都要求先检查真实 PC 当前客户端，再合并共享协议和传输实现，不能把旧一对一草稿当作已完成的 Windows 群组。

当前仓库能在 Mac 上检查 Windows 目标代码、共享流式传输类和 native 配对桥的编译；Windows 端的流式类尚未等同于 Windows 托盘、WinForms、DPAPI、地址变化、多机或跨机 CrossPaste 验收。不能把 Mac 本机测试或协议夹具写成 Windows 群组已完成，也不能声称两端跨机已经验收。请按 [`tests/双机隔离验收.md`](tests/双机隔离验收.md) 分别验证 Mac→PC、PC→Mac、多 PC、重启恢复、DHCP 地址变化以及暂停后的负对照。

## 协议和测试边界

`PROTOCOL.md` 记录 TCP `49287` 内容协议的精确字节布局；新增 TCP `49289` 的流式协议见 [`STREAM_V2.md`](STREAM_V2.md)；发现和配对的跨平台规范见 [`PAIRING_V2.md`](PAIRING_V2.md)。端口、版本、字节序、密码算法和群组协议不能由单端自行改动。`tests/` 中的本机测试使用合成内容、隔离剪贴板和回环网络，不能替代两台真实设备的验收，也不应输出真实密钥、配对码或剪贴板正文。

双机联调前，先按验收文档临时正常退出或暂停 CrossPaste，并确认它不会自动恢复。仅看到对端出现内容不是成功证据；正向结果需要轻剪自己的 ACK 和接收写入证据，暂停状态还要有新内容不传输的负对照。没有 Mac 或 Windows 对端时，只报告已经完成的本机项目，并明确双机联调未完成。

本项目是本地生成的试用软件。Mac 采用临时签名，Windows 尚无发布签名；系统按实际情况显示登录项、网络和安全提示，不自动绕过。程序不自动修改防火墙、路由器、代理或公网端口。
