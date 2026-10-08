# Windows 0.3.0 交接与运行包脱敏审查

审查更新：2026-10-09。Mac 应用保持 0.3.1/build5，不重新编译、安装或读取真实配对信息。Windows 已在 Mac 完成限定范围的元数据脱敏、全包检查和重新打包，用户无需开 PC 重建。

## 原包与源码

原完整交接包 SHA-256 为 `d5df8ac1f1d04f6d3241db52f7fe92146efb51434253229130e001030cb5f462`；外层 52 个文件校验值和两层 ZIP 完整性通过。原运行 ZIP 有 275 个文件，解压总计 125,956,582 字节。原交接包、含个人路径的 DLL 和原运行 ZIP 留在本地，未上传。

完整源码已导入 `windows/LightClip.Group/`、`windows/LightClip/`、`windows/tests/`、`windows/pairing/` 和 `windows/protocol/`。生产入口为 `windows/LightClip.Group/LightClip.Group.csproj`。旧 `windows/LightClip.Windows.csproj` 仅作历史参考；生产 C#、Rust、项目文件、manifest 和图标保持 PC 提供的原字节。

## 脱敏方法与修改范围

最初发现配对 DLL 的 `.rdata` 有三处 Rust Cargo 源位置字符串包含个人 Windows 用户目录，所以暂缓发布。进一步核对确认三处都是 `.rs` 源文件名，由共 10 个 Rust 源位置记录引用；长度、指针、行列字段可验证。它们不位于执行代码，不是配对密钥或密码常量。

`tools/sanitize_windows_release.py` 仅接受已审查的原 ZIP、DLL 和 EXE 的固定 SHA-256。它要求 x64 PE32+、无签名、安全目录和校验和为空；要求恰好三处用户目录前缀位于非执行 `.rdata`、紧接 Cargo registry 路径并由有效源位置记录引用。随后将这三处前缀替换为等长的通用构建目录，占用长度和指针不变。实际只改变 45 字节，原 DLL 长度仍为 231,936 字节。

已逐字节确认 EXE、DLL 的 `.text`、`.data`、`.pdata`、`.fptable`、`.reloc`、PE 头和全部 16 个 PE 数据目录不变，导入/导出未变。原 275 个文件中只有这一 DLL 的非执行元数据改变，其余 274 个文件完全一致。输出 ZIP 重新压缩，ZIP 压缩流和部分容器属性与原包不同；不声称整个 ZIP 逐字节一致。新增唯一文件为 `RELEASE_PRIVACY_VERIFICATION.json`，记录方法、哈希和未进行 Windows 复测的边界。

全包 276 个条目经过 UTF-8、对齐/非对齐 UTF-16LE/BE 扫描，未发现个人用户目录或所检查的凭据模式；文件列表无本机配对配置、DPAPI、日志、接收缓存或 PDB。标准 .NET 依赖及运行配置为程序依赖，完整保留。四项工具防误用检查通过：拒绝代码被改过的 DLL、非原始 DLL、输出覆盖输入、报告覆盖输入。另一代理独立只读复核了修改范围、PE、全包文件字节和路径扫描。

| 文件 | SHA-256 |
|---|---|
| 原 Windows 运行 ZIP（未上传） | `6122fc5a3f68c861fd2cb57402607d294e27773df51212205f18426dbae3d64b` |
| 脱敏运行 ZIP | `3d9b622c9505b5efacaccb7f6acd658b1f19ddad1d613102331974c9cea4c9cc` |
| 脱敏 native DLL | `724cc9c32727ec7da653ca9d814541576c6ae1a5201da78398e6d5a4e82cef77` |
| 未改动的 LightClip.exe | `d564e1d6d18a1b05d6ed6cfabd05ae5b64ec19a673f859602c8f395ba9bacec8` |

脱敏 ZIP 为 50,467,626 字节、276 个文件。它是原 PC 运行文件的元数据脱敏包，并非新编译产物；脱敏后 Windows 真机运行未测，静态一致性不能替代该复测。新哈希和该边界均已记录，没有沿用旧 ZIP 哈希或宣称原验收覆盖新包。

未来从源码构建已在 `windows/build.ps1` 增加 Rust 路径映射，保留静态 CRT 和现有参数，发布前拒绝残留个人用户目录的 native DLL。此更新脚本尚未在 Windows 重新构建验证；它用于以后从源码构建的预防，不是本次运行包的生成方式。详见 `windows/PRIVACY_REBUILD.md`。

## 兼容性与测试基线

PC 的 `PairingCrypto.cs`、`StreamProtocol.cs`、`StreamTransfer.cs`、Rust 源码/锁文件和三份协议与 Mac 侧已有实现逐字节一致。Mac 使用 .NET 10 SDK 对完整 Windows 生产入口交叉编译通过，零警告、零错误；额外六项流式检查和链接实际共享类的配对/流式核心夹具通过。这没有运行 Windows GUI、托盘或 DPAPI。

PC 报告 Windows 11 x64 上群组 81 项、流式 23 项、最终剪贴板 22 项、Rust 4 项、额外 6 项通过，完整 5 GiB 本机回环实际传输、SHA 校验和 done 完成，耗时 45.45 秒。81 项群组 JSON 仍标历史 0.2.0，不能称 0.3.0 全量重跑。图片跨机使用来自用户反馈，保留的元数据窗口无独立 kind=2 事件。以上为原 PC 二进制基线，Mac 未重复执行 Windows 测试。

PC 报告启用空闲样本峰值 Private Bytes 25.25 MiB、工作集 86.09 MiB；5 GiB 本机测试峰值 Private Bytes 171.41 MiB、工作集 207.43 MiB。不同场景不能当作常驻内存或跨机吞吐保证。

用户反馈双向文字、图片、文件可用。真实双机暂停负对照、第二台真实 PC、实际 DHCP/重启/物理休眠、磁盘耗尽和跨机精确边界未形成完整矩阵。项目保持私有预览，Windows 未签名，Mac 临时签名、未公证；保留第三方声明，未另行选择顶层项目许可证。
