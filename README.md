# 轻剪 · LightClip（Mac 0.3.1 / Windows 0.3.0）

轻剪是一个只在局域网工作的文字、图片和普通文件双向剪贴板同步工具。0.3.1 / build 5 保留 0.3.0 的 `LightClip/v1` 兼容限制和 `STREAM_V2` 文件/大文字流式传输：严格 UTF-8 文字最多 10 MiB，单次最多 32 个普通文件，单文件和整批总量均最多 5 GiB。文件按 256 KiB 块读取或写入，每块写盘、更新 SHA-256 并收到 ACK 后才继续；接收完成前不改剪贴板。没有云端中继、剪贴板历史或离线内容队列。

协议分工固定为：UDP `49286` 负责未认证的局域网发现，TCP `49288` 负责一次性短码 PAKE 配对，TCP `49287` 继续承载 `LightClip/v1` 的文字/PNG/ACK，TCP `49289` 承载 `LightClip/stream/v2` 的长文字和普通文件。完整的新增流式字节布局、manifest、超时、缓存、取消和验收边界见 [`STREAM_V2.md`](STREAM_V2.md)；旧内容协议仍以 [`PROTOCOL.md`](PROTOCOL.md) 为准。

## 当前仓库包含什么

| 部分 | 状态 |
|---|---|
| Mac 客户端 | 0.3.1 / build 5 菜单栏应用，群组配对、文字/图片/文件同步 |
| Windows 群组客户端 | PC 交接的 0.3.0 完整 WinForms/Win32 实现，入口为 `windows/LightClip.Group/LightClip.Group.csproj` |
| Windows 共享组件及测试 | 已导入 PC 群组、剪贴板、流式与互操作夹具；配对和流式共享类与原实现逐字节一致 |
| 旧 Windows UI 参考实现 | `windows/LightClip.Windows.csproj` 及同目录旧草稿仅保留作历史参考，不能作为生产入口 |

[下载 Mac 和 Windows 预览版](https://github.com/oceanliangcn716/LightClip/releases/tag/v0.3.1-cross-platform-preview)。Mac 包适用于 Apple Silicon，Windows 包为 x64 自包含程序，无需用户安装 .NET 或 Rust。Windows 解压完整目录后双击 `LightClip.exe`；Mac 解压后将“轻剪.app”放入应用程序目录。首次使用时，两台设备连到同一局域网，一台创建群组，另一台从附近设备列表选择它并输入其界面显示的 8 位配对码。下载包不包含作者的配对信息，每位用户自行建立群组。当前仓库为私有预览，需要仓库访问权限。

用户已反馈自己的 Mac 与升级后的 PC 双向文字、图片、文件可用。PC 完整源码已经导入；Windows 运行包的三处 Rust 源文件名中的个人目录前缀已在 Mac 上做等长脱敏，共改变 45 个非执行元数据字节。EXE、DLL 机器码和其余 274 个原始文件逐字节不变；重新压缩 ZIP 并记录了新的校验值。脱敏后的 Windows 包没有进行真机复测，原 PC 测试只作为原运行文件的基线。完整过程见 [`WINDOWS_IMPORT_REVIEW.md`](WINDOWS_IMPORT_REVIEW.md)。旧 release 的 handoff 和重建工具仅供历史开发参考，普通用户下载新的运行包即可。

## 从源码构建

Mac 构建需要 Xcode Command Line Tools 和 Rust 工具链。在仓库根目录运行：

```sh
zsh build-mac.sh
```

产物是 `dist/轻剪.app`，按构建机器生成 arm64 或 x86_64 程序；当前提供的 Mac 二进制为 **Apple Silicon / arm64**。Swift 的部署目标、Rust 部署环境及应用最低系统标注统一为 macOS 14.0；当前运行测试在开发机完成，macOS 14 真机和 Intel Mac 尚未运行验证。构建结果为临时签名，不包含 Apple 公证或发布证书。升级已有本地安装时，macOS 可能要求重新确认钥匙串访问。

Windows 完整群组客户端需要 .NET 10 SDK、官方 Rust MSVC 工具链、`x86_64-pc-windows-msvc` target、Visual C++ 构建工具和 Windows SDK。在 Windows 仓库根目录运行 `pwsh -File windows/build.ps1 -RebuildNative`，生成 x64 自包含运行目录。构建脚本映射 Rust 编译路径并检查 native DLL 不含个人用户目录。仓库不提交预编译 DLL；修改后的脚本仍需在真实 PC 执行验证。当前运行包是原 PC 已测版本的元数据脱敏包，并非通过此更新脚本重新编译；未来从源码构建使用路径映射防止再次带入个人目录。当前没有 ARM64 Windows 运行包。

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

旧版本的手动配对可以由 Mac 保留已有钥匙串密钥并按迁移边界继续使用，但这不等于已经加入 0.2.0 群组。新版 Windows 下载包已经包含群组实现；旧一对一客户端需要升级后加入群组。

## 0.3.0 文件流基线与本地验证边界（0.3.1 保留）

0.3.1 / build 5 本轮只调整 Mac 菜单栏常驻和设置窗口的启动行为；下面保留 0.3.0 文件流功能及其验证边界。当前主线已完成一次本机回环的完整 5 GiB 流式传输：`5368709120` 字节、接收 SHA-256 一致、完成 ACK，耗时约 11.23 秒；同进程 sender/receiver 的最大 RSS 约 `17,448,960` 字节（16.6 MiB，属于传输自测进程，不是 GUI 内存测量）。这只是回环和实现级证据，不能替代真实网络吞吐、Windows 真机或两台设备验收。10 MiB 文字边界、空文件、多文件、取消清理及双向 Swift/C# 流式互通已通过本机夹具；真实两机仍应按 `STREAM_V2.md` 和 `tests/双机隔离验收.md` 复核。

## Windows 交接边界

Windows 生产入口是 `windows/LightClip.Group/LightClip.Group.csproj`；`windows/LightClip/` 包含它链接的 Win32 剪贴板、内容协议和 DPAPI 公共文件。`windows/tests/` 是 PC 原始夹具，`windows/pairing/` 保留原 Rust 源码与锁文件，`windows/protocol/` 与仓库根协议逐字节一致。`windows/Program.cs` 和 `windows/LightClip.Windows.csproj` 为旧一对一参考版，不能作为生产入口。根目录跨平台配对和流式夹具已改为链接实际群组项目的共享类；旧 v1 互操作夹具仍使用历史参考实现，真实 Windows v1/STA 行为以 PC 报告和后续真机复核为准。

历史群组和文件升级交接材料仍保留供追踪；完整客户端已经导入，不必重新套用旧草稿。本次 Windows 运行包已在 Mac 完成限定范围的源位置脱敏和静态复核，用户无需开 PC 重建。以后修改源码时使用 `windows/build.ps1` 构建，并按 `windows/PRIVACY_REBUILD.md` 做开发者发布检查。

Mac 侧已确认收到的完整 Windows 生产项目编译通过，零警告、零错误，并检查共享协议一致性。交接包报告 Windows 群组 81 项、流式 23 项、最终剪贴板 22 项、Rust 4 项和额外 6 项通过；这些是 PC 提供的结果，不能写成 Mac 代理亲自运行了 Windows。PC 报告中明确没有补做真实双机暂停负对照；第二台真实 PC、物理恢复和专项边界也未完整验收。保留这些边界，不把本机回环或用户反馈写成全矩阵通过。

## 协议和测试边界

`PROTOCOL.md` 记录 TCP `49287` 内容协议的精确字节布局；新增 TCP `49289` 的流式协议见 [`STREAM_V2.md`](STREAM_V2.md)；发现和配对的跨平台规范见 [`PAIRING_V2.md`](PAIRING_V2.md)。端口、版本、字节序、密码算法和群组协议不能由单端自行改动。`tests/` 中的本机测试使用合成内容、隔离剪贴板和回环网络，不能替代两台真实设备的验收，也不应输出真实密钥、配对码或剪贴板正文。

双机联调前，先按验收文档临时正常退出或暂停 CrossPaste，并确认它不会自动恢复。仅看到对端出现内容不是成功证据；正向结果需要轻剪自己的 ACK 和接收写入证据，暂停状态还要有新内容不传输的负对照。没有 Mac 或 Windows 对端时，只报告已经完成的本机项目，并明确双机联调未完成。

本项目是本地生成的试用软件。Mac 采用临时签名，Windows 尚无发布签名；系统按实际情况显示登录项、网络和安全提示，不自动绕过。程序不自动修改防火墙、路由器、代理或公网端口。
