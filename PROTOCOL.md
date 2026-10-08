# LightClip/v1（轻剪）跨平台协议

本文件只定义兼容保留的 TCP `49287` `LightClip/v1` 内容协议。LightClip 0.3.0 另外使用 TCP `49289` 承载大于 1 MiB 的文字和普通文件；其固定字节布局、AES-GCM 帧、manifest、块 ACK、SHA-256、取消和缓存边界见 [`STREAM_V2.md`](STREAM_V2.md)。新增流式协议不能放宽或改写本文件的 v1 文字、PNG、ACK、时间戳和重放限制。

LightClip 把“发现、配对、群组”与“内容同步”分成三个平面：IPv4 UDP `49286` 发现，TCP `49288` 进行一次性 8 位短码 PAKE，TCP `49287` 使用本文件定义的 v1，TCP `49289` 使用 [`STREAM_V2.md`](STREAM_V2.md)。发现公告是未认证的便利信息，不能单独证明设备或群组身份；认证和群组密钥交换必须按 `PAIRING_V2.md` 执行。

## 发现和配对入口

发现公告是每个 UP/RUNNING/BROADCAST、非 loopback IPv4 接口的子网广播，不应只向 255.255.255.255 或某个静态地址发送。每 5 秒公告，20 秒未见即过期；收到新设备时最多每秒立即响应一次。公告 JSON 不超过 1024 字节，字段为版本 v:2、类型 type:"lightclip-discovery"、小写带连字符的 deviceId、UTF-8 最多 96 字节且无控制字符的非空 name，以及可选的小写 groupId。接收端只信任 recvfrom 的源 IPv4 地址，排除首字节为 0、127 或 224 及以上的地址；最多保留 64 个设备。设备 IP 变化时以新的发现源地址更新，不保存需要用户手工维护的固定 peer IP。

配对 TCP 端口固定为 49288。每个连接以 4 字节大端 JSON 长度开头，长度必须为 1..4096，并正确处理 TCP 分片；整条连接共用 5 秒绝对期限，最多 4 个入站会话。A 是加入者，B 是邀请者。8 位 ASCII 数字短码只在邀请端内存和配对界面中出现，有效 120 秒，成功一次后失效，每次邀请最多接受 8 次连接尝试。必须使用 Rust spake2 = 0.4.0 的 Ed25519Group 与共享 C ABI；身份字符串、148 字节 transcript、HKDF/HMAC 确认和 76 字节 AES-GCM 群组密钥包裹见 PAIRING_V2.md，不得替换为另一种曲线、PAKE 或自定义密码协议。配对成功后，加入者使用邀请端发送的同一个 groupID 和随机 32 字节 groupKey，两端不能各自创建不同群组。

## 内容连接和帧

两端对每个当前剪贴板内容建立一个新的 TCP 49287 连接，发送一个请求和一个 ACK 后关闭。没有断线重试、离线队列或历史补发；同一份请求 UUID 可在一次多设备发送中扇出，但每个连接仍必须独立完成 ACK。读写必须处理 TCP 分片。监听端最多同时保留 4 个入站连接；发送端最多 4 个在途内容连接。所有连接和完整帧读写使用 5 秒绝对截止时间，包括慢速发送，不因 UI 派发无限延长。

帧为：

    uint32_be(envelope_length) || envelope

长度字段只计算 envelope，不包括自身 4 字节。长度先校验再分配缓冲区，范围为最小 57 字节到最大 8,388,665 字节。

    envelope = nonce[12] || ciphertext[N] || tag[16]

每次发送使用新的 12 字节随机 nonce。加密算法为 AES-256-GCM；AAD 是 UTF-8 字节 LightClip/v1，不包含结尾零字节。群组密钥必须恰好 32 字节。解密失败、长度越界或认证失败时不得修改剪贴板，也不得返回未加密错误细节。

解密后的明文布局如下：

| 偏移 | 长度 | 含义 |
|---|---:|---|
| 0 | 4 | ASCII LCP1 |
| 4 | 16 | 请求 UUID，RFC4122/network byte order |
| 20 | 8 | uint64 大端 Unix 毫秒时间戳 |
| 28 | 1 | kind：0 ping、1 text、2 PNG、3 ACK |
| 29 | 剩余 | body |

kind 0 和 kind 3 的 body 必须为空；请求不能使用 kind 3。kind 1 必须是严格合法 UTF-8、无 BOM，不做换行或 Unicode 归一化，body 最多 1,048,576 字节。kind 2 是完整 PNG 文件字节，body 最多 8,388,608 字节，必须有合法 PNG signature 和长度为 13 的 IHDR，宽高大于 0 且宽×高不超过 16,000,000；写入剪贴板前还必须完整解码，不能只依赖头部。总明文上限为 8 MiB + 29 字节，封套额外包含 nonce 和 tag 共 28 字节。

接收端先校验封套、解密、magic、UUID、时间戳、kind、body 大小和 PNG，再检查进程内重放窗口。时间戳与本机当前时间相差不得超过 120,000 毫秒。最近 5 分钟内已接受的 UUID 最多 4096 个；仍在有效期内达到 4096 个时拒绝新的请求，不得提前驱逐有效保护。暂停和恢复不能清空重放窗口；重启后只剩 120 秒时间窗保护，本版不保存跨重启的 UUID 数据。

只有验证通过并在 UI/STA 线程成功写入剪贴板后，接收端才返回加密 ACK。ACK kind 必须为 3，沿用原请求 UUID，时间戳为 ACK 生成时的当前时间，body 为空。kind 0 ping 不改剪贴板但仍返回 ACK。发送端只有在解密并验证 ACK、且 UUID 与原请求一致后，才把状态显示为“发送成功”。任何无效请求都必须在剪贴板未改变时拒绝。

## 剪贴板边界

使用系统剪贴板变更通知（Mac 原生变更计数；Windows 应使用 AddClipboardFormatListener/WM_CLIPBOARDUPDATE），不能用轮询代替通知。首次未启用、暂停或没有群组时不读取剪贴板、不监听内容。远程写入要记录本次 sequence/changeCount，并抑制由该写入产生的所有本地变更，避免回环；暂停关闭 listener 和内容连接，恢复不补发暂停期间旧内容。

读取前优先排除 concealed、transient 和密码管理器私有格式，包括 org.nspasteboard.TransientType、org.nspasteboard.ConcealedType、application/x-keepassxc-private。FileDrop 只有恰好一个本地普通 PNG/JPG/JPEG 文件时才可读取并转成合法 PNG；源文件最多读取 64 MiB，解码前尽量校验尺寸，最终 PNG 仍受 8 MiB 和 16M 像素限制。多选文件、目录、其他文件类型和带有文件列表的普通路径均跳过；图片优先于附带的 URL 或 UnicodeText。图片传递的是内容，不传原文件名，不在桌面创建文件。程序不保存正文、图片历史、路径或密钥；读取和写入剪贴板遇到忙碌时只能在当前 sequence 仍有效的短窗口内有限重试。

## 密钥保存、迁移和实现边界

Mac 将群组密钥保存到专用钥匙串条目；Windows 群组实现应使用当前用户 DPAPI 加密保存。旧版本手动配对的 Mac 钥匙串密钥可以按迁移逻辑保留，但它不是新群组的常规用户流程；Windows 必须完成群组升级后，才能按 49286/49288 加入同一群组。不要把旧长密钥、真实配对码或剪贴板正文写入聊天、命令行、日志或交接材料，也不能由单端改端口或协议来“兼容”。

当前 windows/Program.cs 仍是一对一 WinForms 客户端草稿，虽包含单 PNG/JPG 文件复制补丁，但没有完成 Windows 群组 UI、发现、自动重连和真实运行验收。windows/PairingCrypto.cs 是可复用的配对加密桥，不代表 Windows 群组客户端已经完成。Mac 本机协议测试、native 桥测试和回环夹具不能替代真实 Windows、多 PC、重启、DHCP 地址变化或 CrossPaste 隔离验收；验收步骤见 tests/双机隔离验收.md。
