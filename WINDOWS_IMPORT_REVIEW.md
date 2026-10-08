# Windows 0.3.0 完整交接审查

审查日期：2026-10-08。Mac 应用保持 0.3.1/build5，不重新编译或安装。

## 收到的内容与状态

PC 提供了完整群组客户端源码和 Windows x64 自包含运行 ZIP。原交接包 SHA-256 为 `d5df8ac1f1d04f6d3241db52f7fe92146efb51434253229130e001030cb5f462`。外层 52 个文件校验值、外层 ZIP 完整性和内层运行 ZIP 完整性通过；运行 ZIP 共 275 个文件，解压总计 125,956,582 字节。

完整源码已经导入 `windows/LightClip.Group/`、`windows/LightClip/`、`windows/tests/`、`windows/pairing/` 和 `windows/protocol/`。生产入口为 `windows/LightClip.Group/LightClip.Group.csproj`。旧 `windows/LightClip.Windows.csproj` 保留作历史参考。

当前没有上传原始完整交接包、原 native DLL 或 Windows 运行 ZIP。运行包需要在 PC 上完成下面的编译脱敏和复核后才能提供给普通用户。

## 隐私问题与修正

`lightclip_pairing.dll` 的 `.rdata` 中有三条 Rust Cargo 依赖源码位置，包含个人 Windows 用户目录。它们是编译时的源位置字符串，不是 PDB 引用，单纯删除 PDB 或 strip 调试信息不能解决。原运行 ZIP 包含同一 DLL。审查记录不保留实际路径值。

当前源码未包含真实配对配置、DPAPI 文件、接收缓存或剪贴板正文。运行时配置 `LightClip.runtimeconfig.json`、依赖描述和标准 .NET 运行时文件属于运行依赖，不能误删。

`windows/build.ps1` 已增加 Rust 路径映射：用 Cargo 编码参数保留含空格的路径，覆盖源码目录、用户目录及自定义 Cargo/Rustup 目录，同时保留静态 CRT 设置；发布前检查 native DLL 中的个人用户目录前缀。新脚本没有在 Mac 上执行 Windows native 构建，仍需 PC 验证。仓库不提交预编译 DLL。

修复应在 PC 重新编译，不能直接修改二进制字节后沿用原来的校验和或验收结论。操作步骤见 `windows/PRIVACY_REBUILD.md`。

## 已核对的兼容性

PC 的 `PairingCrypto.cs`、`StreamProtocol.cs`、`StreamTransfer.cs`、Rust 源码/锁文件及三份协议文档，与 Mac 侧已有实现逐字节一致。导入的生产 C#、Rust、项目文件、manifest 和图标保持原字节。

Mac 侧使用 .NET 10 SDK 对完整 Windows 生产入口交叉编译通过，零警告、零错误；额外六项流式错帧及缓存预留检查通过。此处不声称 Windows EXE 在 Mac 上运行过。

## PC 提供的测试结果

交接报告称 Windows 11 x64 上群组 81 项、流式 23 项、最终剪贴板 22 项、Rust 4 项、额外 6 项通过；完整 5 GiB Windows 本机回环经过实际字节传输、SHA 校验和 done，耗时 45.45 秒。这些结果由 PC 提供，Mac 代理没有重复运行 Windows STA、托盘或 DPAPI 真机测试。

81 项群组回归 JSON 仍标记历史 0.2.0，不能称为 0.3.0 全部重跑。图片的真实跨机使用来自用户反馈，保留的元数据窗口没有独立 kind=2 事件。

PC 报告与用户反馈支持实际启用时双向文字、图片、文件可用；不等于完整隔离矩阵。真实双机暂停负对照没有补做；第二台真实 PC、实际 DHCP/重启/物理休眠、磁盘耗尽以及跨机精确边界未完成。旧测试结果只能作为原二进制基线，重建后的 DLL 必须记录新哈希及实际复测结果。

PC 报告的启用空闲样本为 Private Bytes 峰值 25.25 MiB、工作集 86.09 MiB；完整 5 GiB 本机测试峰值 Private Bytes 171.41 MiB、工作集 207.43 MiB。测量场景不同，不应当作常驻内存或跨机吞吐保证。

本项目仍为私有预览；Windows 运行包未签名，Mac 安装包为临时签名、未公证。总体许可证尚未另行选择，保留第三方声明。
