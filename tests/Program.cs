using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LightClip.Windows;

// Public synthetic fixtures. This executable never opens the system clipboard.
byte[] key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
int testPort = int.TryParse(Environment.GetEnvironmentVariable("LIGHTCLIP_TEST_PORT"), out int port) ? port : 49287;
void Require(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Reject(Action action) { bool rejected = false; try { action(); } catch { rejected = true; } Require(rejected); }
async Task<byte[]> ReadFrame(NetworkStream stream, CancellationToken token) {
    byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
    uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
    Require(length >= 57 && length <= LightClipProtocol.MaxEnvelopeBytes);
    byte[] result = new byte[4 + length]; header.CopyTo(result, 0);
    await stream.ReadExactlyAsync(result.AsMemory(4), token); return result;
}
async Task Request(LightClipPacket packet, byte[]? overrideKey = null, bool fragmented = false) {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    using TcpClient client = new(); await client.ConnectAsync(IPAddress.Loopback, testPort, timeout.Token);
    using NetworkStream stream = client.GetStream();
    byte[] bytes = LightClipProtocol.EncodeFrame(packet, overrideKey ?? key);
    if (fragmented) {
        for (int i = 0; i < bytes.Length; i += 7) await stream.WriteAsync(bytes.AsMemory(i, Math.Min(7, bytes.Length-i)), timeout.Token);
    } else await stream.WriteAsync(bytes, timeout.Token);
    var ack = LightClipProtocol.DecodeFrame(await ReadFrame(stream, timeout.Token), key);
    Require(ack.Kind == LightClipPacketKind.Ack && ack.RequestId == packet.RequestId);
}
async Task RejectRequest(LightClipPacket packet, byte[]? wrongKey = null) {
    bool rejected = false; try { await Request(packet, wrongKey); } catch { rejected = true; } Require(rejected);
}
if (args.Contains("server")) {
    using TcpListener listener = new(IPAddress.Loopback, testPort); listener.Start();
    Console.WriteLine("DOTNET_READY");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    int texts = 0, images = 0, pings = 0;
    for (int i = 0; i < 3; i++) {
        using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
        using NetworkStream stream = client.GetStream();
        var packet = LightClipProtocol.DecodeFrame(await ReadFrame(stream, timeout.Token), key);
        if (packet.Kind == LightClipPacketKind.Text) { Require(LightClipProtocol.DecodeText(packet.Body) == "Mac → .NET 中文 🖥️\nnew line"); texts++; }
        if (packet.Kind == LightClipPacketKind.Png) { Require(packet.Body.SequenceEqual(png)); images++; }
        if (packet.Kind == LightClipPacketKind.Ping) pings++;
        await stream.WriteAsync(LightClipProtocol.EncodeFrame(LightClipProtocol.CreateAck(packet.RequestId), key), timeout.Token);
    }
    Require(texts == 1 && images == 1 && pings == 1);
    Console.WriteLine("PASS: .NET decoded Swift text/image/ping and sent ACK");
    return;
}
var text = LightClipProtocol.CreateText("Windows → Mac 中文 🖥️\r\nnew line", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
var encoded = LightClipProtocol.EncodeFrame(text, key);
var decoded = LightClipProtocol.DecodeFrame(encoded, key);
Require(decoded.RequestId == text.RequestId && decoded.Body.SequenceEqual(text.Body));
Reject(() => LightClipProtocol.DecodeFrame(encoded, new byte[32]));
byte[] changed = (byte[])encoded.Clone(); changed[20] ^= 1; Reject(() => LightClipProtocol.DecodeFrame(changed, key));
Reject(() => LightClipProtocol.DecodeFrame(encoded.AsSpan(0, 20), key));
Reject(() => LightClipProtocol.CreateText(new string('x', LightClipProtocol.MaxTextBytes + 1)));
Reject(() => LightClipProtocol.DecodeText(new byte[] {255}));
Reject(() => LightClipProtocol.CreatePng(new byte[40]));
byte[] largePng = (byte[])png.Clone(); BinaryPrimitives.WriteUInt32BigEndian(largePng.AsSpan(16,4), 16_000_001);
Reject(() => LightClipProtocol.CreatePng(largePng));
var replay = new LightClipReplayWindow(); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
Require(replay.TryAdd(text.RequestId, now)); Require(!replay.TryAdd(text.RequestId, now));
await Request(text, fragmented: true);
await RejectRequest(text); // exact replay
await Request(LightClipProtocol.CreateText(new string('x', LightClipProtocol.MaxTextBytes)));
await Request(LightClipProtocol.CreatePng(png));
await Request(LightClipProtocol.CreatePing(Guid.NewGuid()));
await RejectRequest(LightClipProtocol.CreatePing(Guid.NewGuid()), new byte[32]);
await RejectRequest(LightClipProtocol.CreatePing(Guid.NewGuid(), timestampMilliseconds: 0));
// Oversized header is rejected before allocating a body.
using (TcpClient c = new()) {
    await c.ConnectAsync(IPAddress.Loopback, testPort); using var s = c.GetStream();
    await s.WriteAsync(new byte[]{255,255,255,255});
    using var deadline = new CancellationTokenSource(6000); byte[] b = new byte[1];
    bool closed = false; try { closed = await s.ReadAsync(b, deadline.Token) == 0; } catch (IOException) { closed = true; }
    Require(closed);
}
Console.WriteLine("PASS: .NET→Swift fragmented text, 1MiB text, PNG, ping/ACK; replay/wrong-key/stale/oversized rejected; .NET validation");
