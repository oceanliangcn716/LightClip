# LightClip 配对与群组协议 V2

本文档描述当前 Mac 实现、Rust 配对库和 Windows `.NET 10` 桥接实现共同使用的群组协议。实现必须把局域网发现、临时配对和剪贴板内容传输视为三个不同的平面：发现只负责找到设备，配对负责建立群组密钥，内容传输只使用已经保存的群组密钥。

端口和版本如下：

| 平面 | 生产端口 | 传输 | 用途 |
|---|---:|---|---|
| 局域网发现 | UDP `49286` | IPv4 子网广播 | 发现设备名字、设备 UUID 和可选群组 UUID |
| 群组配对 | TCP `49288` | TCP | 用一次性的 8 位配对码建立群组成员关系 |
| 剪贴板内容 | TCP `49287` | TCP + AES-256-GCM | 在已配对设备之间传输文字、PNG 和 ACK |

生产端口不可由一端单方面改动。测试时必须使用独立 loopback 端口；当前配对夹具使用 `127.0.0.1:49388`，内容协议夹具使用过 `49387`，`49386` 等同类端口也只能用于测试。测试端口、固定合成配对码和测试群组密钥不能用于真实配对。

## 设备和群组身份

每台设备有持久化的 `deviceID` 和用户可修改的 `name`。设备名字使用 UTF-8 编码，不能是空字符串或全空白，最多 96 字节，不能包含 Unicode 控制字符。设备 UUID 的文本形式必须是小写、带连字符的 RFC 4122 `D` 格式，例如 `00112233-4455-6677-8899-aabbccddeeff`。

一个群组由：

- `groupID`：UUID；
- `groupKey`：创建群组时随机生成的 32 字节密钥。

`pairKey` 是一次配对会话的临时密钥，只用于配对确认和包裹 `groupKey`，不得替代或持久化为内容传输密钥。配对完成后，双方只持久化 `groupID` 和 `groupKey`，并用该 `groupKey`保护 TCP 49287 的内容协议。

角色固定如下：

- **A：加入者**，发起加入已有群组的一端，Rust/C ABI 的 `role = 0`；
- **B：邀请者**，已经拥有群组并显示配对码的一端，Rust/C ABI 的 `role = 1`。

## 一、局域网发现平面

### UDP 报文

发现使用 UDP `49286`。Mac 当前实现使用非阻塞 BSD IPv4 UDP socket，设置 `SO_BROADCAST` 和 `SO_REUSEADDR`，绑定 `0.0.0.0:49286`，在主线程的 `DispatchSourceRead` 上读取。

报文是 UTF-8 JSON，整个 UDP 数据报最多 1024 字节，字段为：

```json
{"v":2,"type":"lightclip-discovery","deviceId":"<lowercase-uuid>","name":"<device-name>","groupId":"<lowercase-uuid>"}
```

没有群组时省略 `groupId`；不能发送 `groupId: null` 作为跨实现的替代形式。字段含义如下：

| 字段 | 要求 |
|---|---|
| `v` | 必须是整数 `2` |
| `type` | 必须是 `lightclip-discovery` |
| `deviceId` | 必须是小写带连字符 UUID；收到自身 UUID 时丢弃 |
| `name` | 非空、非全空白、UTF-8 不超过 96 字节、无控制字符 |
| `groupId` | 缺省或小写带连字符 UUID |

未知字段不参与认证，也不能被当作地址或密钥来源。发现报文本身没有认证，任何同一广播域中的程序都可以伪造名字、UUID 或群组 UUID；发现结果只能用于显示候选设备和发起配对，不能据此信任群组成员或传输内容。

### 广播地址、来源地址和心跳

每次公告都重新读取 `getifaddrs`。只选择同时具备 `UP`、`RUNNING`、`BROADCAST` 的非 loopback IPv4 接口，并使用该接口的子网广播地址；不能只向 `255.255.255.255` 广播，也不能把默认 VPN 接口当作唯一局域网出口。重复的广播地址只发送一次。

设备启动后立即公告，之后每 5 秒公告一次。收到一个有效的新设备，或者发现该设备的来源地址发生变化时，向 `recvfrom` 返回的来源 IPv4 地址直接回公告；这类即时响应全局最多每秒一次。JSON 中没有 IP 字段，接收端只能把 `recvfrom` 得到的 IPv4 地址作为 `NearbyDevice.host`，不能信任消息内自带的地址。

每次读取最多处理 64 个 UDP 数据报；超长报文、无效 JSON、版本或类型错误、非规范 UUID、非法名字和来源 IPv4 首字节为 `0`、`127` 或 `224+` 的报文都丢弃。最多保留 64 个发现条目。设备超过 20 秒没有有效心跳就过期。回调结果按 `name` 再按小写 UUID 稳定排序；仅 `lastSeen` 变化不会造成无意义的 UI 刷新。

IP 变化时，设备仍由同一个 `deviceID` 识别，新的 `host` 取自新的 `recvfrom` 来源。生产实现不得把旧的发现 IP 当作永久静态 peer；群组成员端点应随发现结果刷新，最多保留 16 个目的端。Mac 的发现、回调和 UI 更新均在主线程完成。退出应用时关闭发现 socket、读取源和定时器；旧回调不得继续生效。暂停同步时可以继续发现设备，但必须停止内容发送与接收。

## 二、群组配对平面

### TCP 帧和会话边界

生产配对服务监听 TCP `49288`。每条 TCP 连接使用以下帧：

```text
frame = uint32_be(json_length) || json_bytes
```

`json_length` 必须在 1 到 4096（含边界）之间。必须先读取并检查长度，再分配 JSON 缓冲区；读取必须处理任意 TCP 分片。每条连接从建立、读写到关闭共用一个 5 秒绝对截止时间，不能因为收到半个字段或一个新的分片而重新计时。超时、断开、长度错误或 JSON 错误都结束该连接。

JSON 至少包含 `v: 2` 和 `type`。当前 Swift `Codable` 解码器只依赖各流程明确列出的字段，未使用的可选字段不参与密钥派生和认证。接收端必须对流程所需字段、UUID、Base64 解码结果和固定长度进行校验；不要把设备名字、发现报文或配对码当作密钥。

邀请者最多同时保留 4 个入站配对会话。一次邀请的连接尝试最多 8 次；连接在收到无效 hello 后也消耗一次尝试。配对码有效期为 120 秒，且一次邀请成功使用后立即关闭，不能用同一个码继续添加第二台设备。邀请过期、达到尝试上限、手动关闭或配对完成后都不得继续接受该邀请。

### 配对码和角色

配对码是恰好 8 个 ASCII 数字，不能包含空格、Unicode 数字或其他字符。配对码只作为本地调用 native `lc_spake_start` 的参数，存在于参数和临时内存中；不写入日志、配置文件或异常文本，也不作为 JSON 字段直接发送。B 在 UI 中显示配对码，A 通过附近设备选择后输入该码。

### 消息顺序和字段

下面的 Base64 字段均先 Base64 解码，再按表中的字节长度校验。Base64 文本本身不是 transcript 的一部分。

| 顺序 | 方向 | `type` | 必需字段和校验 |
|---:|---|---|---|
| 1 | A → B | `pair-hello` | `deviceId=A 的 client UUID`、`name`、`groupId=目标群组 UUID`、`spake=33 字节 messageA` |
| 2 | B → A | `pair-challenge` | `deviceId=B 的 host UUID`、`groupId`、`spake=33 字节 messageB`、`salt=16 字节`、`proof=32 字节 server-confirm` |
| 3 | A → B | `pair-proof` | `proof=32 字节 client-confirm` |
| 4 | B → A | `pair-result` | `sealed=76 字节的群组密钥包裹` |
| 5 | A → B | `pair-done` | `proof=32 字节 stored` |

消息的完整流程如下：

1. B 已经持有 `groupID` 和随机 `groupKey`，打开一次邀请并开始 120 秒倒计时。
2. A 从发现结果取得 B 的 UUID、来源 IP 和 `groupID`，创建 role 0 的 SPAKE 上下文，发送 `pair-hello`。
3. B 只接受当前邀请、目标 `groupID`、有效名字、不同于自身的 client UUID 和长度为 33 的 messageA。B 创建 role 1 上下文，消费 messageA，生成随机 16 字节 `salt`，计算 transcript、`pairKey` 和 `server-confirm`，发送 challenge。
4. A 校验 challenge 中的 host UUID 和群组 UUID，消费 messageB，使用相同 transcript 派生 `pairKey`，恒时验证 `server-confirm`。
5. A 计算并发送 `client-confirm`。B 恒时验证该 proof。
6. B 用 `pairKey` 包裹 `groupID || groupKey`，发送 `pair-result`，并关闭当前邀请，避免同一配对码再次使用。
7. A 解包并校验期望的 `groupID`，确认得到的 32 字节 `groupKey` 后持久化群组，再发送 `stored` proof。
8. B 验证 `stored` proof，结束会话。任何一步失败都不得把未验证的群组密钥写入持久化存储。

### SPAKE2 native 桥

两端必须使用同一个 Rust C ABI，以避免不同语言各自实现曲线参数、身份绑定或消息编码时产生差异。Rust crate 由 `pairing/Cargo.toml` 和 `Cargo.lock` 固定为：

- crate `spake2 = 0.4.0`；
- `Ed25519Group`；
- `Identity`；
- `Password`；
- release 配置启用尺寸优化、LTO、单 codegen unit 和 strip。

不能把它替换为另一种 SPAKE2 群、另一版 crate、SRP、PAKE 或自定义曲线协议。最终 Windows 用户只需要随程序提供编译好的 `lightclip_pairing.dll`；不需要安装 Rust 或 Cargo。

当前 C ABI 为：

```c
void *lc_spake_start(
    uint8_t role,
    const uint8_t *password, size_t password_len,
    const uint8_t *id_a, size_t id_a_len,
    const uint8_t *id_b, size_t id_b_len,
    uint8_t *message_out);

int32_t lc_spake_finish(
    void *state,
    const uint8_t *peer_message, size_t peer_len,
    uint8_t *key_out);

void lc_spake_destroy(void *state);
```

`lc_spake_start` 只接受 role `0/1`、8 字节 ASCII 数字密码、非空且不超过 256 字节的身份，输出 33 字节公开消息和 opaque state。`lc_spake_finish` 无论 peer 消息有效、错误还是长度错误都会消费 state；成功写出 32 字节 SPAKE 输出并返回 `1`，失败返回 `0`。尚未调用 finish 的 state 由 `lc_spake_destroy` 释放。C# `PairingSpake` 使用 `SafeHandle`、一次性消费标记和 `IDisposable` 实现相同生命周期，不能复用已完成或失败的上下文。

## 三、身份字节和 transcript

### 两个 SPAKE 身份

身份字符串先按下列形式构造，再使用严格 UTF-8 编码；字符串中的 UUID 必须是小写十六进制、带连字符的 `D` 格式：

```text
idA = UTF8("LightClip/pair/v2/client/" + clientUUID.lowercase-with-hyphens)
idB = UTF8("LightClip/pair/v2/host/" + hostUUID.lowercase-with-hyphens + "/" + groupUUID.lowercase-with-hyphens)
```

身份字符串末尾没有额外的 NUL。配对码、身份和长度错误必须使用不包含具体参数值的错误信息。

### UUID 字节序

transcript 中的 UUID 是 16 字节 RFC 4122/network byte order：文本 `00112233-4455-6677-8899-aabbccddeeff` 对应字节 `00 11 22 33 44 55 66 77 88 99 aa bb cc dd ee ff`。Windows `.NET Guid.ToByteArray()` 的默认前 4/2/2 字段是混合小端序，不能直接放进 transcript；`PairingCrypto.cs` 已显式转换。Swift 使用 `UUID.uuid` 的 RFC 字节元组。

### 固定 transcript

先把 message 和 salt 从 Base64 解码，要求 `messageA=33`、`messageB=33`、`salt=16`。然后严格按下列顺序拼接：

```text
T = UTF8("LightClip/pair/v2\0")  // 18 字节
    || clientUUID[16]
    || hostUUID[16]
    || groupUUID[16]
    || messageA[33]
    || messageB[33]
    || salt[16]
```

`T` 总长度固定为 148 字节。不要加入 JSON 字段、名字、Base64 文本、TCP 帧长度、角色字节或其他分隔符。A 和 B 必须使用同一组 client/host/group UUID，以及同一组 messageA/messageB/salt；交换顺序不能颠倒。

## 四、配对密钥、确认和群组密钥包裹

### pairKey

令 `spakeSecret` 为 native `lc_spake_finish` 输出的 32 字节：

```text
pairKey = HKDF-SHA256(
    IKM  = spakeSecret[32],
    salt = salt[16],
    info = T[148],
    L    = 32
)
```

Swift 使用 `HKDF<SHA256>.deriveKey`；Windows `PairingCrypto.DeriveKey` 必须产生完全相同的 32 字节结果。`spakeSecret`、`pairKey` 和 `groupKey` 都不能进入日志、异常文本、调试输出或普通配置文件。`spakeSecret` 与 `pairKey` 不发送到网络；`groupKey` 只能放入经过 AES-GCM 保护的密钥包裹，不能作为明文 JSON 字段发送。

### proof

确认消息统一为：

```text
proof(label) = HMAC-SHA256(
    key = pairKey,
    data = UTF8(label + "\0") || T
)
```

只允许这三个标签：

- `server-confirm`：B 在 challenge 中发送，A 验证；
- `client-confirm`：A 在 pair-proof 中发送，B 验证；
- `stored`：A 在收到并保存群组密钥后发送，B 验证。

验证必须使用恒时比较；proof 长度必须是 32 字节。标签、NUL、transcript 顺序和大小写都不能改变。

### groupKey 包裹

明文严格为：

```text
groupBody = groupUUID[16] || groupKey[32]
```

使用 `pairKey` 作为 AES-256-GCM 密钥，随机生成 12 字节 nonce，AAD 严格为：

```text
AAD = UTF8("LightClip/group-key/v2\0") || T
```

输出 `sealed` 严格为：

```text
sealed = nonce[12] || ciphertext[48] || tag[16]
```

总长度固定为 76 字节。A 解包时必须先完成 GCM 认证，再检查明文中的 group UUID 等于正在加入的 `expectedGroup`，最后复制出恰好 32 字节 `groupKey`。UUID 不匹配、认证失败、长度错误都必须拒绝，并且不能持久化任何结果。

## 五、内容传输平面保持 LightClip/v1 不变

配对只建立群组身份和 32 字节 `groupKey`；内容协议仍然是现有 `LightClip/v1`，版本、端口、字段、字节序和加密参数不能因为加入群组而变化。

每次发送建立一个新的 TCP `49287` 连接，发送一次请求，接收同一连接上的一次 ACK，然后关闭。帧格式为：

```text
frame = uint32_be(envelope_length) || envelope
envelope = nonce[12] || ciphertext[N] || tag[16]
```

AES-256-GCM 的 AAD 是没有终止 NUL 的 UTF-8 字节 `LightClip/v1`，密钥是群组随机 32 字节 `groupKey`。解密明文严格为：

| 偏移 | 长度 | 含义 |
|---:|---:|---|
| 0 | 4 | ASCII `LCP1` |
| 4 | 16 | Packet UUID，RFC 4122/network byte order |
| 20 | 8 | uint64 大端 Unix 毫秒时间戳 |
| 28 | 1 | kind：`0` ping、`1` text、`2` PNG、`3` ACK |
| 29 | 剩余 | body |

kind `0` 和 `3` 的 body 必须为空。文字必须是严格 UTF-8，最多 1 MiB；PNG 最多 8 MiB，签名和 IHDR 有效，宽×高最多 16,000,000 像素，接收端写剪贴板前还必须能完整解码。时间戳与本机当前 Unix 毫秒之差最多 120 秒。请求和 ACK 都用同一群组 key 加密；ACK 的 UUID 必须等于原请求 UUID，只有收到合法 ACK 后才能显示发送成功。

### 群组扇出、重放和回环抑制

- 每次本地剪贴板捕获只创建一个 Packet UUID；向多个成员扇出时复用这个 UUID，不为每个目的端重新创建内容。
- 发现结果最多形成 16 个目的端；图片传输最多 4 个在途连接/图片缓冲，避免群组变大时无限占用内存。
- 接收入站内容最多 4 个并行连接；每个连接的连接、读写和 ACK 使用 5 秒绝对截止时间，必须处理 TCP 分片。
- 没有离线队列、历史内容或断线补发；新的本地内容会取消旧的未完成扇出，不能把过期内容留到恢复连接后发送。
- 每个进程维护最多 4096 个已接受 Packet UUID，条目存活 5 分钟；重复 UUID 拒绝，达到上限时拒绝新 UUID，不能驱逐仍有效的保护记录。
- 远程写入剪贴板后记录序列号/内容哈希，随后由系统产生的剪贴板变更事件必须抑制，不能自动回传；同一个 Packet UUID 从多个成员到达时，重放窗口也会阻止重复写入。
- 暂停必须停止剪贴板读取、关闭内容 listener 和未完成连接，但保留内存中的 ReplayGuard；恢复时不能重新发送旧内容。重启后的重放保护不跨进程持久化，仍受 120 秒时间戳窗口约束。

## 六、持久化和生命周期

已配对设备持久化：

- `deviceID` 和设备名字；
- 当前 `groupID`；
- 当前 32 字节 `groupKey`；
- 用户选择的暂停状态和开机自连设置。

Mac 的群组密钥使用专用 Keychain 条目保存；Windows 使用当前用户 DPAPI 保护的文件保存，不能将明文密钥放进 JSON、注册表普通值、日志、崩溃转储或命令行。设备名字和非秘密状态可放在各自的普通用户配置中。

创建或加入群组成功后，开机自连设置默认配置为启用，用户仍可以在设置中关闭。手动退出群组必须停止配对、内容和发现相关的群组状态，忘记 `groupID` 和 `groupKey`，并取消该群组的开机自连；以后重新加入必须使用新的配对流程。关闭设置窗口或主窗口只隐藏 UI、保留托盘/菜单栏后台服务；只有明确选择“退出”才停止服务并退出进程。

暂停只改变运行状态，不删除群组配置、不清空 ReplayGuard、不生成新的密钥，也不发送暂停期间积累的旧剪贴板内容。发现层可以停止公告和监听，内容层必须关闭 listener；恢复时重新使用已保存群组 key，并等待新的剪贴板变更。

## 七、实现和测试边界

Windows 端直接复用 `windows/PairingCrypto.cs` 的以下 API：

```text
PairingSpake(role, code, clientID, hostID, groupID)
PairingSpake.Message : byte[]        // 33 字节
PairingSpake.Finish(peerMessage)     // 32 字节；一次性消费

PairingCrypto.Transcript(...)
PairingCrypto.DeriveKey(spakeKey, salt, transcript)
PairingCrypto.Proof(key, label, transcript)
PairingCrypto.VerifyProof(...)
PairingCrypto.WrapGroup(groupID, groupKey, key, transcript)
PairingCrypto.UnwrapGroup(expectedGroup, sealed, key, transcript)
```

当前测试夹具 `tests/PairInterop.csproj`/`PairInterop.cs` 的 `native` 模式会加载项目 `pairing/target/release/liblightclip_pairing.dylib`，使用公开的合成 UUID、固定测试码和测试群组 key 验证同码同身份、错码、身份变化、重复 Finish 和 malformed message。`server`/`client` 模式只使用 `127.0.0.1:49388`，验证配对 JSON、TCP 分片、5 秒截止时间和 Mac 配对时序；它们不能当作真实局域网或生产配对证明。

内容协议的 loopback 夹具使用过独立端口 `49387`；`49386`、`49388` 等端口也只能由测试流程占用。所有测试应使用合成密钥和独立的命名剪贴板/loopback 环境，不能把测试码、测试 group key 或测试端口配置带入用户设备。

目前没有完成真实 Windows PC、两台以上实体设备、DHCP 地址变化、系统重启后的完整群组恢复和跨平台生产网络验收。不能把 native 模式或 loopback client/server 的 PASS 描述成真实 PC、多机或重启成功。
