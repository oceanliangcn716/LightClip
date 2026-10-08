# 轻剪 Windows 0.3.0 群组与文件同步

原生 .NET 10 WinForms / Win32 托盘客户端。发布目录自带 .NET 运行时、匹配 x64 的 Rust 配对 DLL 和包内图标，最终用户无需安装 .NET 或 Rust。

## 使用

双击桌面“轻剪 LightClip”或安装目录 LightClip.exe。已有群组与 CurrentUser DPAPI 数据保持原位置，升级无需重新配对。关闭窗口继续托盘；再次启动打开现有实例。登录启动保持已有偏好；默认配对后启用，可在设置中取消并保存。

正常复制文字、截图或普通本地文件即可发送到同群在线设备。收到文字/图片可直接粘贴；收到普通文件可在 Explorer 中粘贴。单张小 PNG/JPG/JPEG 同时提供原文件 FileDrop 和 PNG/DIBV5/DIB 图片格式。文件列表优先于附带文字；多文件按文件批次发送。

托盘和设置提供暂停/恢复、测试群组连接、取消当前传输、打开接收文件夹、明确退出。收到新本地复制、暂停、退出群组或程序退出都会取消未完成流，不离线排队、不补发旧内容。已完成文件保留以支持稍后粘贴；缓存预算10 GiB，不自动删除仍可能被引用的完成文件。

## 协议与合并

UDP49286发现、TCP49288短码SPAKE2配对和TCP49287 LightClip/v1继续沿用。v1小文字仍最多1 MiB，PNG最多8 MiB/1600万像素，AES-GCM、ACK、UUID网络字节序与5秒绝对截止未改变。

新增TCP49289 LightClip/stream/v2，文字最多10 MiB UTF-8；普通文件最多32个，单文件与整批均最多5 GiB。采用包内 StreamProtocol.cs、StreamTransfer.cs、PairingCrypto.cs 原文件，未改协议、字段、密码算法或共享实现。没有用包内一对一 Program.cs 覆盖群组程序。Rust SPAKE2=0.4.0和Cargo.lock保持包内版本。

文件按256 KiB块加密并等待对应chunk-ack；完整核对长度、SHA256、UTF-8和剪贴板版本，提交后才发done。最多2入站/2出站流，10 GiB缓存包含并行预留；目录DACL限定当前用户。普通文件不全量读入内存。拒绝目录、网络共享、reparse point、不安全basename、重复名称、超限和敏感剪贴板格式。

所有剪贴板读写在STA/UI线程，WM_CLIPBOARDUPDATE触发，有限重试、来源标记防回环。配对码/密钥只在正常界面与程序内处理，groupKey由现有DPAPI存储加载。证据仅内存中的时间、类型、长度和结果，不记录正文、正文哈希、真实路径或凭据。防重放窗口暂停时保留，进程重启不持久化。Windows文本剪贴板不支持嵌入NUL，明确拒绝而不截断。

## 构建与测试

LightClip.Group.csproj为生产入口。在仓库根目录运行 `pwsh -File windows/build.ps1 -RebuildNative`，构建 x64 配对库并发布 net10.0-windows/win-x64、自包含、PublishReadyToRun。脚本映射 Rust 的本机源路径，并在发布前检查 DLL 不含个人用户目录。仓库不提交 native DLL。未修改防火墙、路由器、代理或CrossPaste。

--self-test、--stream-self-test、--stream-clipboard-test仅用于隔离的合成测试，覆盖真实Windows STA剪贴板。完整流式测试包含实际5 GiB本机回环与资源采样；剪贴板测试省略已单独完成的大文件。--dpapi-group-probe只用于合成测试的跨进程恢复；--ui-clipboard-fixture仅准备固定合成PNG供人工Explorer粘贴。不要在真实同步期间运行这些测试。

Windows本机结果与双机验收边界见同目录“Windows-0.3.0-验收记录.md”。模拟群组、本机回环与代码存在均不能替代Mac或第二台真实PC验收。真实联调先确认所有旧同步通道退出，再执行两端/单端暂停15秒负对照和正对照；须有轻剪自身加密ACK和接收写入证据。
