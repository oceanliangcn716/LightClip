请在这台 Windows PC 恢复使用后，直接完成轻量剪贴板客户端“轻剪 / LightClip”的群组升级，并做到能编译、安装、启动和本机实测。PC 目前关机，所以 Mac 端先准备协议和交接材料；PC 上线后再执行真实 Windows 验收。不要只给方案、代码片段或编译命令。

优先检查我附上的 `LightClip-Windows-0.3.0-文件同步升级.zip`，并阅读其中的 `PAIRING_V2.md`、`PROTOCOL.md`、`windows/PairingCrypto.cs`、`pairing/Cargo.lock`、`pairing/build-windows.ps1`、`tests` 以及图标资源。先把交接包与这台 PC 当前源码逐项比较。包内 `windows/Program.cs` 仍是已有的一对一客户端草稿，已补普通文件/长文字流接线，但不是已经完成的 Windows 群组 UI；不要盲目覆盖 PC 上以前做好的真实修复、DPAPI 数据、图标、登录启动或配对状态。需要合并时按当前 PC 的实际版本修改，并记录实际差异。

目标是让 Windows 端与 Mac 菜单栏版进入同一个加密群组：首次设备有友好的中文水果风格名字，界面显示局域网附近设备列表；用户点击设备后输入 Mac 显示的 8 位短码即可加入同一群组；同一群组可以继续加入多台 PC；设备 DHCP 或 Wi‑Fi 导致 IP 改变后，靠局域网发现自动更新地址并重连，不要求用户手动维护旧 IP。保留文字双向同步、截图和透明 PNG、普通文件 FileDrop（包括单张本地 PNG/JPG/JPEG 的图片与原文件双格式），以及包内 `assets/LightClip.ico` 的正常 EXE、托盘和设置窗体图标。关闭设置窗口或主窗口后程序继续托盘运行；只有明确点击“退出”才停止同步。

协议必须原样实现，不能改端口、版本、字节序、字段、密码协议或自行重新发明 PAKE：

- UDP `49286` 是未认证的局域网发现；公告 JSON 为 `v:2`、`type:lightclip-discovery`、小写带连字符 `deviceId`、名字和可选 `groupId`。名字 UTF‑8 最多 96 字节。使用每个 UP/RUNNING/BROADCAST 非 loopback IPv4 接口的子网广播，5 秒公告，20 秒过期，来源 IP 只取 `recvfrom`，不能信任 JSON 内地址。
- TCP `49288` 是配对。4 字节大端 JSON 长度必须为 1–4096，处理 TCP 分片，整条连接共用 5 秒绝对截止时间。A 为加入者、B 为邀请者；配对码恰好 8 位 ASCII 数字，有效 120 秒、一次成功使用，每次邀请最多 8 次连接尝试、最多 4 个入站会话。
- 配对必须使用 Rust `spake2 = 0.4.0`、`Ed25519Group` 和包内 C ABI。直接复用 `PairingCrypto.cs` 的 `PairingSpake`、transcript、HKDF-SHA256、三种 HMAC confirmation 和 AES-GCM 76 字节群组密钥包裹。不要用另一种曲线、另一版 crate、SRP、自己写的 PAKE 或把配对码当作网络明文。
- TCP `49287` 的 `LightClip/v1` 内容协议完全不变，继续使用群组随机 32 字节 key、AES-256-GCM、现有 ACK、时间戳、PNG/文字上限和重放规则。配对完成后 A 解出 Mac 发送的同一个 `groupID` 和 `groupKey`；两端不能各自新造不同的 groupID，也不能把旧的一对一长密钥流程当作新群组流程。
- TCP `49289` 是 0.3.0 新增的 `LightClip/stream/v2`，只承载大于 1 MiB 的严格 UTF-8 文字和普通文件；不要把这类内容塞回 49287，也不要修改 49287 的旧上限。完整字节布局和状态流程必须逐字对照仓库根目录的 [`STREAM_V2.md`](STREAM_V2.md)：AES-256-GCM/AAD、LCS2、网络序 GUID、offer→ready→单块 chunk/ACK→finish/哈希→commit→done、每块 256 KiB、单流一块在途、5 秒首帧、认证后 30 秒无进展、24 小时总期限、5 分钟/4096 UUID 重放窗口。

0.3.0 的共享 Windows 流式实现已经放在仓库的 `windows/StreamProtocol.cs` 和 `windows/StreamTransfer.cs`，测试入口是 `tests/StreamInterop.cs` / `tests/StreamInterop.csproj`。先检查 PC 上真实客户端是否已有自己的改动，再按实际差异合并共享的 `StreamProtocol`、`StreamTransfer`、`StreamItem`、`StreamManifest`、`StreamRecord` 和 `ReceivedStream`，不能用包内旧的一对一 `Program.cs` 覆盖现有群组代码，也不能凭空重写一套不兼容的流协议。公开接线签名是 `SendFilesAsync(host, port, key, paths, progress, cancellationToken)`、`SendTextAsync(host, port, key, text, progress, cancellationToken)` 和 `ReceiveAsync(TcpClient, key, cacheRoot, commit, cancellationToken)`；`commit` 正常返回后才发送 done。

实现界面时保持原生、轻量和中文：托盘显示状态、暂停/恢复、测试群组连接、发送当前剪贴板、打开设置和退出；显示本机地址、设备名、附近设备、群组状态和错误原因，但不显示密钥、配对码以外的敏感材料。第一次未配对时不读剪贴板、不启动内容监听；完成配对并保存后默认启用登录/开机自连，用户可以明确关闭。暂停要关闭剪贴板监听、内容 listener 和未完成连接，但保留群组配置和 replay 防重放状态；恢复不能补发暂停期间旧内容。手动退出群组要停止同步、本地忘记 `groupID` 和 `groupKey`、取消该群组的自动启动；原有 DPAPI 和旧配对数据不能为了升级随意清空，先保留并明确迁移边界。旧长密钥不再作为普通用户新流程，也不要把真实旧密钥发到 Codex 或写进聊天、命令行和日志。

继续保留剪贴板安全边界：使用 Windows `AddClipboardFormatListener`/`WM_CLIPBOARDUPDATE`，剪贴板读写只在 STA/UI 线程；排除 concealed、transient、密码管理器私有格式。FileDrop 的普通文件支持 1–32 个；恰好一个本地普通 PNG/JPG/JPEG 时还要读取并转为合法 PNG，其他普通文件和多选通过文件流发送，目录、符号链接和非法路径才跳过。源图片最多读取 64 MiB，解码前检查尺寸，最终 PNG 不超过 8 MiB 且不超过 1600 万像素。图片优先于附带 URL/UnicodeText。大文字严格按 UTF-8 处理，最多 10 MiB；普通文件单文件和整批最多 5 GiB，文件按块读取，不得 `ReadAllBytes` 大文件。收到文件时先写入私有缓存临时目录，所有块写盘并做 SHA-256 后再原子改名；文件提交给 Windows FileDrop/Explorer 后保留缓存目录。文字的 `commit` 回调必须在返回前读完临时 `clipboard.txt`、写入普通文字剪贴板；共享接收器在 commit 成功后删除文字暂存目录，不能在回调返回后继续引用它。远程写入后抑制由本次写入产生的本地变更，不能回环；不保存历史，不记录正文、路径、密钥或配对码。

先检查 PC 是 x64 还是 ARM64，再用 `pairing/build-windows.ps1 -Architecture x64` 或 `arm64` 编译随包 native DLL，并确认 `cargo build --locked --release` 使用 `Cargo.lock`。最终用户不应安装 Rust；发布目录必须带正确架构的 `lightclip_pairing.dll`、EXE、图标和必要运行时。使用项目的 .NET 10 WinForms 目标编译，检查单实例、DPAPI CurrentUser、托盘图标、设置窗体、暂停/退出清理和安装启动路径。不要改防火墙、路由器、系统代理或开放公网端口；确需 Windows 防火墙入站权限时先完成代码和本机验证，再向我说明并请求最小范围确认，不能绕过系统签名或安全提示。

验证必须分层记录：先跑 `tests` 中的 native/协议夹具和错误密钥、篡改、分片、过期、重复 UUID、超限、ACK 对应测试；流式夹具应覆盖 49399 server/client、2 MiB 多块 payload + 空文件、10 MiB 文字边界、哈希/偏移/序号/取消清理。再在 Windows 本机测试托盘、友好名字、附近列表、短码加入、两台以上群组成员、v1 文字/emoji/多行、截图、透明 PNG、单 PNG/JPG 文件、49289 长文字、普通文件 FileDrop、Explorer 粘贴、目录和非法 FileDrop 排除、暂停恢复、DPAPI 重启恢复、地址变化自动重连和远程不回环。必须另外在 Windows 做一次真实 5 GiB 合成文件低内存传输，记录 Private Bytes/工作集、CPU、耗时和 SHA-256；不要把 Mac 回环结果写成 Windows 实测。还要验证中断、暂停、新本地复制、空间不足、10 GiB 缓存预算、并发预留和重启后的清理。

双机测试前临时正常退出两端 CrossPaste，并确认进程没有自动重启；做轻剪暂停的负对照，再做 Mac→PC、PC→Mac 和多 PC 正对照。覆盖 49287 小内容与 49289 大内容的两个方向、文件原字节、文字临时文件删除、文件缓存保留。只看到对端内容或网络可达不算成功，必须同时有轻剪自己的有效 ACK、接收写入证据和暂停后不传输证据。双机隔离按 [`tests/双机隔离验收.md`](tests/双机隔离验收.md) 执行，不能把 CrossPaste 的结果归给轻剪。

如果 Mac 或另一台 PC 当前不在线，完成到能完成的 native、WinForms、本机剪贴板和安装验证，并明确列出“两端联调未完成”；不要假设通过，不要编造内存、地址变化、重启或多机结果。最终告诉我实际安装目录、启动方式、Windows 架构、native DLL 构建结果、已通过的本机/双机项目、未完成项目和我下一步最少需要做的操作。不要让工具、终端、日志、聊天或交接材料暴露真实密钥、动态配对码、剪贴板正文或路径；程序按现有设计正常通过 Windows 当前用户 DPAPI / Mac Keychain 访问已保存密钥。若 Mac 或另一台 PC 不在线，明确标出“两端联调未完成”，不要声称未实测项目已完成。
