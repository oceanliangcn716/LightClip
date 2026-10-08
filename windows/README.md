# LightClip Windows 0.3.0

生产入口是 `LightClip.Group/LightClip.Group.csproj`。这是已安装验证的群组实现，支持发现、8位短码配对、多成员发送、DPAPI、托盘、文字/图片以及 stream/v2 文件传输。`LightClip/` 只包含生产入口链接的公共 Win32/协议/DPAPI 文件，不是另一套一对一启动入口。

## 运行

最终运行包采用 `LightClip-Windows-0.3.0-win-x64.zip`，解压整个目录，双击 `LightClip.exe`。适用于 Windows x64；自带 .NET 和 x64 配对 DLL，用户无需安装 Rust 或 .NET。不要单独拿走 EXE 或省略语言资源目录。2026-10-08 收到的交接二进制还残留 Rust 编译个人路径，当前暂缓上传；具体状态见仓库根目录 `WINDOWS_IMPORT_REVIEW.md`。

设备名自动保存，通过附近设备选择 Mac 并在正常界面输入其配对码。配置在当前用户 LocalAppData/LightClip/group-v2，秘密由 DPAPI CurrentUser 加密；安装包不包含任何设备的配对数据。已有用户升级保留这个目录。新用户独立配对，不能复制别人的 DPAPI 数据。关闭窗口继续托盘运行。

文字最多10MiB UTF-8；普通文件最多32个，单个及整批最多5GiB。支持截图、透明PNG、单张PNG/JPG/JPEG文件与图片双格式。大文件完整接收、校验、提交后更新剪贴板；完成前粘贴仍可能是上一项。提供取消和打开接收文件夹，不离线排队补发。

## 在 Windows 构建

需要 .NET SDK 10、Rust MSVC 工具链、`x86_64-pc-windows-msvc` target、MSVC Build Tools 和 Windows SDK。仓库不提交 native DLL；首次从源码构建请在本目录运行：

```powershell
pwsh -File ./build.ps1 -RebuildNative
```

输出 `artifacts/LightClip-0.3.0-win-x64/`，完整目录即自包含应用。也可以在 Windows PowerShell 中运行脚本；本包没有修改机器执行策略。SDK不在PATH时使用 `-DotNetPath <dotnet.exe路径>`。

脚本重新构建 Rust DLL 时映射个人用户目录、Cargo/Rustup 和源码目录，避免 Rust panic 源位置包含个人路径。发布前还会拒绝包含个人用户目录的 DLL。已有自行构建且检查通过的 x64 DLL 时，可省略 `-RebuildNative`：

```powershell
pwsh -File ./build.ps1
```

脚本使用原始 Cargo.lock 和静态CRT，仅构建 x64。本次没有构建或验收 ARM64。`pairing/build-windows.ps1` 保留上游原件供参考；它原有输出布局为 windows/native，推荐用根目录 build.ps1 与当前群组项目衔接。

## 测试

```powershell
pwsh -File ./test-protocol.ps1
```

该脚本使用合成测试值/随机临时密钥与回环端口，运行配对 native、流式 native/text/negative 和额外6项检查，不访问系统剪贴板或真实配对数据。测试中的公开固定 key 是上游互操作夹具，不是用户密钥，不得用于真实配对。

群组和真实STA剪贴板测试在应用内：`--self-test <结果JSON>`、`--stream-clipboard-test <结果JSON>`、`--stream-self-test <结果JSON>`。必须在隔离测试会话退出生产客户端/其他同步软件后运行，不要覆盖正在使用的剪贴板。群组夹具需设置 LIGHTCLIP_TEST_DOTNET 与 LIGHTCLIP_INTEROP_DLL 指向SDK和已构建的 tests/Interop 夹具。完整流式测试会实际生成并传输5GiB；现有 StreamTests.cs 使用 D:/LightClipTestTemp，复现机器需具备该盘及足够空间，或仅调整测试临时目录。生产代码不依赖D盘。

协议原件见 `protocol/`。共享 StreamProtocol.cs、StreamTransfer.cs、PairingCrypto.cs 保持上游字节一致；不能改端口、格式、字节序或密码参数。Rust crate 的0.2.0版本号是原始配对库版本，桌面应用为0.3.0。

验收记录与资源测量在交接包 `docs/`。本机81项群组、23项流式、22项最终剪贴板与实际5GiB检查通过；用户确认真实启用后的双向文字/图片/文件可用，并观察到自身文字/文件ACK与Windows写入。真实双机暂停负对照未测，用户决定停止补测；第二真实PC、专项边界与物理恢复仍未完成，不能称完整验收通过。生产启用空闲实测Private25.25MiB、工作集86.09MiB，工作集未达到50MB目标。

本项目的总体许可证沿用目标仓库的授权决定；本包未擅自新建主项目许可证。Rust crate声明MIT；第三方声明在 LightClip.Group/THIRD_PARTY_NOTICES.txt，发布运行包也包含该文件。
