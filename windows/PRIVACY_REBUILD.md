# Windows 运行包编译路径脱敏

Mac 审查发现原 `lightclip_pairing.dll` 含三条 Rust 依赖源码个人目录路径。源码已经导入主仓库；当前不发布原运行 ZIP。修正仅涉及编译和打包，不改协议、功能、文件上限或既有用户配对。

## PC 上执行

1. 使用最新 `windows/build.ps1`。如在原 PC 工程工作，把此脚本放在 `LightClip.Group`、`LightClip`、`pairing`、`tests` 的共同父目录。不要用旧一对一草稿覆盖生产程序。
2. 在该目录运行 `pwsh -File ./build.ps1 -RebuildNative`；若没有 pwsh，使用现有 Windows PowerShell 执行等价命令，不修改全局执行策略。脚本需要原有 .NET 10 SDK、Rust MSVC、x64 target、MSVC Build Tools 和 Windows SDK。
3. 确认重新生成的 native DLL 是 x64，静态 CRT 仍保留；扫描 ASCII/UTF-8、UTF-16LE/BE 字符串，不得包含个人用户目录、实际凭据或配置。源码里的公开测试 key 与用户真实群组密钥必须区分，不能读取真实密钥作比对。
4. 运行 `test-protocol.ps1` 中的 native 配对、流式 native/text/negative 和额外六项夹具；执行 Rust 四项测试。仅使用合成内容和临时配对，不读取系统剪贴板正文。
5. 如验证新程序启动，先等待现有传输完成，通过应用自己的退出流程切换；确认新进程确实加载新目录里的 EXE 和 DLL，避免被旧实例的单实例转发误导。保留原 DPAPI、群组配置、开机启动偏好，不重置配对，不输出真实设备和群组标识。
6. 打包完整自包含输出目录，包含运行时、资源目录、图标、native DLL、说明和第三方声明。新 Windows 运行 ZIP 的版本可保持 0.3.0；不要仅打包 EXE，也不要带入 profile、DPAPI、日志、接收缓存、真实路径或剪贴板数据。
7. 更新运行包和完整交接包的 SHA-256 清单及验证 JSON，记录新的 DLL/EXE 哈希、实际重跑项目与未测边界。原来的 81/23/22 项、5 GiB 回环等若未重跑，应明确标为旧二进制基线。

返回一份 `LightClip-Windows-0.3.0-完整交接包-脱敏版.zip`，包含修正源码、完整 Windows x64 运行 ZIP、校验清单及精简验证记录。不要返回或公开实际个人路径值。Mac 应用和现有配对不需要修改。
