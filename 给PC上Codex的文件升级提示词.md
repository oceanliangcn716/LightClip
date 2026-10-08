# 给 PC 上 Codex 的文件升级提示词

请在这台 Windows PC 上把现有的“轻剪 / LightClip”群组客户端升级到 0.3.0 的文件和大文字流式传输，并完成能编译、安装、启动和实际验证的 Windows 版本。不要只给方案、代码片段或编译命令。PC 可能已经有 0.2 群组、DPAPI、托盘、剪贴板和图片修复；先检查真实 PC 当前源码和运行状态，再合并本提示词要求。不要用仓库里的旧一对一 `windows/Program.cs` 覆盖 PC 已有实现，也不要回退已经完成的群组功能。

先完整阅读仓库根目录的 `STREAM_V2.md`、`PROTOCOL.md`、`PAIRING_V2.md`，并参考本地 `windows/Program.cs` 新增的文件/长文字接线（保留 PC 现有群组实现），以及 `windows/StreamProtocol.cs`、`windows/StreamTransfer.cs`、`tests/StreamInterop.cs`、`tests/StreamInterop.csproj`。0.3.0 的 Mac 流式实现已经完成；主线曾完成一次本机回环 5 GiB 传输，大小为 `5368709120` 字节、SHA-256 一致、收到完成 ACK，耗时约 11.23 秒，同进程 sender/receiver 最大 RSS 约 16.6 MiB。这些是实现级和回环证据，不能写成 Windows 真机、真实网络或两台设备已验收；PC 必须重新编译和实测自己的结果。

端口和用途必须保持固定：

- UDP `49286`：未认证的 IPv4 局域网发现。公告是便利信息，不能作为身份认证；使用实际 `recvfrom` 源地址，不能相信 JSON 内的地址。
- TCP `49287`：现有 `LightClip/v1` 小文字、PNG、ping 和 ACK。v1 的 1 MiB 文字、8 MiB PNG、1600 万像素、时间戳、ACK、重放和 5 秒连接限制不能改变。
- TCP `49288`：一次性 8 位短码 PAKE 配对。继续使用 `PAIRING_V2.md`、Rust `spake2 = 0.4.0`、`Ed25519Group`、包内 C ABI、transcript、HKDF/HMAC confirmation 和群组密钥包裹。
- TCP `49289`：`LightClip/stream/v2` 大文字和普通文件流。不得把大文字或文件偷偷改回 49287，也不能自定义另一套帧格式。

流式协议必须原样执行 `STREAM_V2.md`：AES-256-GCM 使用已配对的 32 字节 groupKey，AAD 是无结尾零的 UTF-8 `LightClip/stream/v2`；每条记录使用独立随机 nonce；帧是 `uint32_be(envelope_length) || nonce12 || ciphertext || tag16`，封套最小 61、最大 262217 字节。明文是 `LCS2`、RFC4122 网络字节序传输 UUID、uint64 大端 Unix 毫秒、uint32 大端序号、1 字节 kind、正文。严格执行 `offer(seq=0)`、`ready(seq=0)`、顺序 `chunk`/`chunk-ack`、`finish`/SHA-256、commit 后 `done` 的流程；每条连接只允许一块在途，块大小 1–262144 字节。首帧认证绝对期限 5 秒，认证后连续 30 秒无网络读写进展取消，总期限 24 小时。保留至少 5 分钟/4096 UUID 的重放保护，并在暂停/恢复时保留保护状态。

把共享实现按仓库当前实际文件合并到 PC 客户端，不要凭空重写协议。需要保留的公开类型和职责包括 `StreamProtocol`、`StreamTransfer`、`StreamItem`、`StreamManifest`、`StreamRecord` 和 `ReceivedStream`。当前共享传输接口为：

```csharp
Task SendFilesAsync(
    string host,
    int port,
    ReadOnlyMemory<byte> key,
    IReadOnlyList<string> paths,
    IProgress<double>? progress = null,
    CancellationToken cancellationToken = default);

Task SendTextAsync(
    string host,
    int port,
    ReadOnlyMemory<byte> key,
    string text,
    IProgress<double>? progress = null,
    CancellationToken cancellationToken = default);

Task ReceiveAsync(
    TcpClient client,
    ReadOnlyMemory<byte> key,
    string cacheRoot,
    Func<ReceivedStream, Task> commit,
    CancellationToken cancellationToken = default);
```

`ReceivedStream` 提供 `Manifest` 和完成目录中的 `Paths`。`commit` 正常返回后才能发送 `done`；失败、取消、哈希错误、空间不足、缓存超限或剪贴板版本已变化时不得发送 `done`，并清理部分文件。文字回调必须在返回前读完临时 `clipboard.txt`、严格按 UTF-8 写入普通文字剪贴板；共享 ReceiveAsync 在提交回调成功后会删除文字暂存目录；普通文件提交为 Windows FileDrop/Explorer 可粘贴的文件后，完成目录保留在应用缓存中，并提供“打开接收文件夹”。

实现文件和大文字入口时遵守这些边界：

1. 普通文件最多 32 个，单文件和整批总量最多 5 GiB；文字最多 10 MiB UTF-8。文件名必须是单个安全 basename，拒绝路径穿越、分隔符、控制字符、尾部空格/点、Windows 保留名、重复 basename、目录、符号链接、reparse point、UNC/网络共享路径。
2. 发送端在打开前和发送后检查普通文件身份、大小、修改时间；途中源文件改变不得显示成功。不得对大文件调用 `ReadAllBytes`，每流只保留一个约 256 KiB 块和加密开销。发送端每个块必须等待对应 `chunk-ack` 才能发送下一块。
3. 接收端使用当前用户私有的随机 UUID 临时目录和独占创建的文件，不跟随符号链接；计算 10 GiB 缓存预算，并把并行接收预留计入预算。每块写盘、Flush、更新 SHA-256 后 ACK；finish 时核对每项大小、总量、SHA-256 和文本 UTF-8，再在同一缓存根内原子改名。
4. 单张小 PNG/JPG/JPEG 文件既要保留原文件字节供 FileDrop/Explorer 粘贴，也可以在 Windows 剪贴板提供图片格式；PNG 必须完整解码，继续遵守 v1 的 8 MiB 和 1600 万像素限制。多选、目录、非图片文件列表不能被误判为图片。
5. 所有剪贴板读写必须在 WinForms STA/UI 线程通过 `AddClipboardFormatListener`/`WM_CLIPBOARDUPDATE` 完成；收到远程内容前记录本机剪贴板版本，提交前再次检查，版本变化就取消提交并清理。远程写入产生的通知必须抑制，不能回环；暂停、退出群组、应用退出、新本地复制都要取消未完成流，不自动重试、不补发旧剪贴板。
6. groupKey 只能从现有 Windows 当前用户 DPAPI 群组配置中取得，不把密钥、配对码、剪贴板正文、正文哈希或真实路径写入命令行、日志、普通配置或交接材料。协议负向测试只使用仓库公开的合成夹具，不把合成值当成真实群组密钥。

启动监听前调用 `StreamTransfer.CleanupAbandonedTransfers(cacheRoot)` 清理本应用上次异常中断的临时目录；不要删除已完成且可能仍被剪贴板引用的文件。真实 UI 必须提供可读的失败、超限和缓存不足提示（共享接收器会关闭失败连接，须在客户端 catch 后显示一般原因且不能泄漏路径或密钥）。并发上限由生产调用方限制为最多 2 入站、2 出站。

不要把文件流接成另一个独立的一对一客户端：它必须使用现有群组发现得到的 peer 地址、配对得到的同一 groupID/groupKey、暂停/恢复状态和现有托盘生命周期。保留中文轻量 WinForms 托盘界面、设备名、附近设备、群组状态、测试群组连接、设置、暂停/恢复、打开接收文件夹和明确退出；关闭窗口只隐藏到托盘。不要用 Electron、浏览器内核、数据库、OCR、历史记录或网络中继。先确认 PC 是 x64 还是 ARM64，再用正确架构构建 `pairing/build-windows.ps1` 和 native DLL；发布目录要包含匹配架构的 `lightclip_pairing.dll`、EXE、图标和所需运行时，不能要求最终用户安装 Rust。不要修改防火墙、路由器、代理或开放公网端口；确需 Windows 入站权限时，先完成代码和本机验证，再说明最小范围请求。

验证按以下顺序进行，所有运行输出都不得暴露真实密钥、动态配对码、剪贴板正文或用户路径：

1. 编译 `.NET 10 WinForms` Windows 项目和 Rust native 配对桥，确认架构、锁定依赖、DPAPI 当前用户保存、单实例、托盘图标和登录启动路径。先运行 `tests/StreamInterop.csproj` 的 `native`、`text`、`negative` 模式；`server`/`client` 必须成对在隔离端口运行，不能单独启动后把等待对端超时判为协议失败；49399 仅用于独立协议夹具，不得混入生产 49289。覆盖错误 key、篡改、过期、错误序号/偏移、manifest 越界、路径穿越、保留名、重复 basename、空文件、多文件、哈希错误、取消清理和 ACK 对应关系。
2. 在 Windows 本机用 STA 实际测试 v1 小文字/emoji/多行、截图、透明 PNG、单 PNG/JPG、目录和非法 FileDrop 排除，以及 49289 的 10 MiB 文字、多块文件、空文件、Explorer 粘贴和接收文件夹。验证文字临时目录在提交后删除、普通文件缓存按设计保留。
3. 在临时缓存目录生成合成 5 GiB 文件做一次 Windows 真实流传，记录传输方向、总字节、SHA-256、完成 ACK、耗时、Private Bytes/工作集、CPU 和缓存占用；测量失败或 PC 未在线时明确写“Windows 5 GiB 实传未完成”，不要引用 Mac 回环数字冒充结果。另测中断、暂停、新本地复制、空间不足、10 GiB 缓存预算、并发预留和重启后的临时数据清理。
4. 双机联调前按 `tests/双机隔离验收.md` 临时正常退出或暂停两端 CrossPaste，并确认它没有自动恢复；不卸载、不删除 CrossPaste 数据、不改变旧软件永久设置。先做轻剪暂停的 15 秒负对照，再做 Mac→PC、PC→Mac、多 PC 的 v1 与 v2 正对照，验证文字、图片、文件原字节、ACK、Explorer/Finder 粘贴、暂停恢复、断线后新内容、DHCP 地址变化和重启恢复。只看到对端出现内容或端口可达不算成功，必须有轻剪自身 ACK 和接收写入证据。

最终报告必须列出：实际 PC 架构、源码合并差异、安装目录和启动方式、native DLL 构建结果、Windows 本机通过项目、5 GiB 低内存测量口径、双机方向和隔离结果、未完成项目，以及用户下一步最少需要做的操作。PC、Mac 或另一端不在线时，继续完成独立工作，但明确标出“两端联调未完成”；绝不能把共享源码存在、Mac 回环或测试夹具通过写成 Windows 群组或真实双机已经完成。
