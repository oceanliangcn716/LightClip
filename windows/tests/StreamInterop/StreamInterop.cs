using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LightClip.Windows;

const int FixtureLength = 2_097_169;
byte[] key = Enumerable.Range(0, StreamProtocol.AesKeyBytes).Select(value => (byte)value).ToArray();
string mode = args.Length == 0 ? "native" : args[0].ToLowerInvariant();

try
{
    switch (mode)
    {
        case "native":
            await RunNativeAsync(key);
            break;
        case "server":
            await RunServerAsync(key);
            break;
        case "client":
            await RunClientAsync(key);
            break;
        case "text":
            await RunTextAsync(key);
            break;
        case "negative":
            await RunNegativeAsync(key);
            break;
        default:
            throw new InvalidOperationException();
    }
}
catch
{
    // Deliberately avoid paths, exception details, message bodies, and key material.
    Console.WriteLine("FAIL: stream interop");
    Environment.ExitCode = 1;
}

static async Task RunNativeAsync(byte[] key)
{
    Require(StreamProtocol.Port == 49289);
    Require(StreamProtocol.MaxTextBytes == 10 * 1024 * 1024);
    Require(StreamProtocol.MaxFileBytes == 5_368_709_120UL);
    Require(StreamProtocol.ChunkBytes == 262_144);
    Require(StreamProtocol.MaxEnvelopeBytes == 262_217);

    Guid id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    var offer = new StreamManifest("files", new[]
    {
        new StreamItem("payload.bin", FixtureLength),
        new StreamItem("empty.txt", 0),
    }, FixtureLength);
    byte[] manifestBytes = StreamProtocol.EncodeManifest(offer);
    StreamManifest decodedManifest = StreamProtocol.DecodeManifest(manifestBytes);
    Require(decodedManifest.Type == offer.Type && decodedManifest.Total == offer.Total && decodedManifest.Items.SequenceEqual(offer.Items));
    Require(StreamProtocol.EncodeManifest(new StreamManifest("files", new[] { new StreamItem("big.bin", StreamProtocol.MaxFileBytes) }, StreamProtocol.MaxFileBytes)).Length < StreamProtocol.MaxManifestBytes);
    Require(StreamProtocol.EncodeManifest(new StreamManifest("text", new[] { new StreamItem("clipboard.txt", (ulong)StreamProtocol.MaxTextBytes) }, (ulong)StreamProtocol.MaxTextBytes)).Length < StreamProtocol.MaxManifestBytes);

    Reject(() => StreamProtocol.DecodeManifest(Utf8("{\"v\":2,\"type\":\"files\",\"items\":[{\"name\":\"a\",\"size\":18446744073709551616}],\"total\":0}")));
    Reject(() => StreamProtocol.DecodeManifest(Utf8("{\"v\":2,\"type\":\"files\",\"items\":[{\"name\":\"a\",\"size\":2},{\"name\":\"A\",\"size\":0}],\"total\":2}")));
    Reject(() => StreamProtocol.DecodeManifest(Utf8("{\"v\":2,\"type\":\"files\",\"items\":[{\"name\":\"CON.txt\",\"size\":0}],\"total\":0}")));
    Reject(() => StreamProtocol.DecodeManifest(Utf8("{\"v\":2,\"type\":\"files\",\"items\":[{\"name\":\"../x\",\"size\":0}],\"total\":0}")));
    Reject(() => StreamProtocol.DecodeManifest(Utf8("{\"v\":2,\"type\":\"text\",\"items\":[{\"name\":\"clipboard.txt\",\"size\":10485761}],\"total\":10485761}")));
    Reject(() => StreamProtocol.DecodeManifest(new byte[] { 0x7b, 0x22, 0x76, 0x22, 0x3a, 0xff }));
    Reject(() => StreamProtocol.ValidateManifest(new StreamManifest("files", new[] { new StreamItem("a", StreamProtocol.MaxFileBytes) }, StreamProtocol.MaxFileBytes - 1)));
    Reject(() => StreamProtocol.ValidateManifest(new StreamManifest("files", new[] { new StreamItem("a", 1), new StreamItem("A", 0) }, 1)));

    var ready = new StreamRecord(id, 0, StreamRecordKind.Ready, ReadOnlySpan<byte>.Empty);
    byte[] frame = StreamProtocol.EncodeFrame(ready, key);
    Require(frame.Length == 4 + StreamProtocol.MinEnvelopeBytes);
    StreamRecord decoded = StreamProtocol.DecodeFrame(frame, key);
    Require(decoded.Id == id && decoded.Sequence == 0 && decoded.Kind == StreamRecordKind.Ready && decoded.Body.Length == 0);
    Reject(() => StreamProtocol.DecodeFrame(frame, Enumerable.Repeat((byte)0xff, 32).ToArray()));
    byte[] altered = (byte[])frame.Clone();
    altered[^1] ^= 1;
    Reject(() => StreamProtocol.DecodeFrame(altered, key));
    Reject(() => StreamProtocol.DecodeFrame(frame, key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 121_000));
    Reject(() => StreamProtocol.DecodeFrame(new byte[] { 0, 0, 0, 1 }, key));

    byte[] maxChunkBody = new byte[StreamProtocol.ChunkMetadataBytes + StreamProtocol.ChunkBytes];
    BinaryPrimitives.WriteUInt32BigEndian(maxChunkBody.AsSpan(0, 4), 0);
    BinaryPrimitives.WriteUInt64BigEndian(maxChunkBody.AsSpan(4, 8), 0);
    byte[] maxChunkFrame = StreamProtocol.EncodeFrame(new StreamRecord(id, 1, StreamRecordKind.Chunk, maxChunkBody), key);
    Require(maxChunkFrame.Length == 4 + StreamProtocol.MaxEnvelopeBytes);

    string root = MakeTempDirectory();
    string sourceRoot = Path.Combine(root, "source");
    string cacheRoot = Path.Combine(root, "cache");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(cacheRoot);
    string payloadPath = Path.Combine(sourceRoot, "payload.bin");
    string emptyPath = Path.Combine(sourceRoot, "empty.txt");
    await WriteFixtureAsync(payloadPath, FixtureLength);
    await File.WriteAllBytesAsync(emptyPath, Array.Empty<byte>());
    Reject(() => StreamTransfer.SendFilesAsync("127.0.0.1", 1, key, new[] { sourceRoot }));
    Reject(() => StreamTransfer.SendFilesAsync("127.0.0.1", 1, key, new[] { "\\\\server\\share\\file.bin" }));
    string linkPath = Path.Combine(sourceRoot, "link.bin");
    try
    {
        File.CreateSymbolicLink(linkPath, payloadPath);
        Reject(() => StreamTransfer.SendFilesAsync("127.0.0.1", 1, key, new[] { linkPath }));
    }
    catch (PlatformNotSupportedException)
    {
        // Symlink creation is unavailable on restricted test hosts.
    }
    catch (IOException)
    {
        // Symlink creation is unavailable on restricted test hosts.
    }
    catch (UnauthorizedAccessException)
    {
        // Symlink creation is unavailable on restricted test hosts.
    }

    using (var listener = new TcpListener(IPAddress.Loopback, 0))
    {
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await StreamTransfer.ReceiveAsync(accepted, key, cacheRoot, received => VerifyReceivedAsync(received, FixtureLength), deadline.Token);
        });
        await StreamTransfer.SendFilesAsync("127.0.0.1", port, key, new[] { payloadPath, emptyPath }, cancellationToken: deadline.Token);
        await server;
    }

    string abandoned = Path.Combine(cacheRoot, ".lcs2-temp-" + Guid.NewGuid().ToString("N"));
    string untouched = Path.Combine(cacheRoot, ".lcs2-temp-unrelated");
    Directory.CreateDirectory(abandoned);
    Directory.CreateDirectory(untouched);
    StreamTransfer.CleanupAbandonedTransfers(cacheRoot);
    Require(!Directory.Exists(abandoned) && Directory.Exists(untouched));
    Require(Directory.EnumerateDirectories(cacheRoot, ".lcs2-complete-*").Any());
    Directory.Delete(root, recursive: true);
    Console.WriteLine("PASS: native codec, manifest limits, C# sender/receiver multi-block fixture");
}

static async Task RunServerAsync(byte[] key)
{
    string root = MakeTempDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 49_399);
    listener.Start();
    Console.WriteLine("DOTNET_READY");
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using TcpClient client = await listener.AcceptTcpClientAsync(deadline.Token);
        await StreamTransfer.ReceiveAsync(client, key, root, received => VerifyReceivedAsync(received, FixtureLength), deadline.Token);
        Console.WriteLine("PASS: server received deterministic multi-block fixture");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RunClientAsync(byte[] key)
{
    string root = MakeTempDirectory();
    try
    {
        string payloadPath = Path.Combine(root, "payload.bin");
        string emptyPath = Path.Combine(root, "empty.txt");
        await WriteFixtureAsync(payloadPath, FixtureLength);
        await File.WriteAllBytesAsync(emptyPath, Array.Empty<byte>());
        string host = Environment.GetEnvironmentVariable("LIGHTCLIP_STREAM_HOST") ?? "127.0.0.1";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await StreamTransfer.SendFilesAsync(host, 49_399, key, new[] { payloadPath, emptyPath }, cancellationToken: deadline.Token);
        Console.WriteLine("PASS: client sent deterministic multi-block fixture");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RunTextAsync(byte[] key)
{
    string root = MakeTempDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    string text = new string('a', StreamProtocol.MaxTextBytes);
    Task server = Task.Run(async () =>
    {
        using TcpClient accepted = await listener.AcceptTcpClientAsync(deadline.Token);
        await StreamTransfer.ReceiveAsync(accepted, key, root, received =>
        {
            Require(received.Manifest.Type == "text" && received.Manifest.Total == StreamProtocol.MaxTextBytes);
            Require(received.Paths.Count == 1);
            Require(new FileInfo(received.Paths[0]).Length == StreamProtocol.MaxTextBytes);
            return Task.CompletedTask;
        }, deadline.Token);
    });
    await StreamTransfer.SendTextAsync("127.0.0.1", port, key, text, cancellationToken: deadline.Token);
    await server;
    Directory.Delete(root, recursive: true);
    Console.WriteLine("PASS: text 10MiB boundary");
}

static async Task RunNegativeAsync(byte[] key)
{
    byte[] filePayload = Encoding.UTF8.GetBytes("synthetic-stream-payload");
    StreamManifest fileManifest = new("files", new[] { new StreamItem("payload.bin", (ulong)filePayload.Length) }, (ulong)filePayload.Length);

    await RunRejectedManualAsync(key, fileManifest, async (network, id, cancellationToken) =>
    {
        await SendOfferAndReadyAsync(network, id, fileManifest, key, cancellationToken);
        await SendChunkAndReadAckAsync(network, id, 1, 0, filePayload, key, cancellationToken);
        byte[] wrongHash = SHA256.HashData(filePayload);
        wrongHash[0] ^= 1;
        await SendRecordAsync(network, new StreamRecord(id, 2, StreamRecordKind.Finish, wrongHash), key, cancellationToken);
    });

    await RunRejectedManualAsync(key, fileManifest, async (network, id, cancellationToken) =>
    {
        await SendOfferAndReadyAsync(network, id, fileManifest, key, cancellationToken);
        await SendRecordAsync(network, new StreamRecord(id, 1, StreamRecordKind.Chunk, ChunkBody(0, 1, filePayload)), key, cancellationToken);
    });

    await RunRejectedManualAsync(key, fileManifest, async (network, id, cancellationToken) =>
    {
        await SendOfferAndReadyAsync(network, id, fileManifest, key, cancellationToken);
        await SendRecordAsync(network, new StreamRecord(id, 2, StreamRecordKind.Chunk, ChunkBody(0, 0, filePayload)), key, cancellationToken);
    });

    StreamManifest invalidTextManifest = new("text", new[] { new StreamItem("clipboard.txt", 1) }, 1);
    await RunRejectedManualAsync(key, invalidTextManifest, async (network, id, cancellationToken) =>
    {
        await SendOfferAndReadyAsync(network, id, invalidTextManifest, key, cancellationToken);
        await SendRecordAsync(network, new StreamRecord(id, 1, StreamRecordKind.Chunk, ChunkBody(0, 0, new byte[] { 0xff })), key, cancellationToken);
    });

    await RunRejectedManualAsync(key, fileManifest, async (network, id, cancellationToken) =>
    {
        await SendOfferAndReadyAsync(network, id, fileManifest, key, cancellationToken);
        // The sender closes this connection immediately after the ready record.
    });

    await RunCancelledReceiverAsync(key, fileManifest);
    await RunValidTextCleanupAsync(key);
    await RunOversizeSourceRejectionAsync(key);
    await RunCacheBudgetRejectionAsync(key);

    Console.WriteLine("PASS: negative SHA/offset/sequence/UTF-8/close/cancel cleanup, text cleanup, metadata/cache limits (commit callback is non-STA)");
}

static async Task RunRejectedManualAsync(
    byte[] key,
    StreamManifest manifest,
    Func<NetworkStream, Guid, CancellationToken, Task> drive)
{
    string root = MakeTempDirectory();
    TcpListener? listener = null;
    TcpClient? sender = null;
    Task? receiverTask = null;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    bool commitCalled = false;
    try
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        receiverTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await StreamTransfer.ReceiveAsync(accepted, key, root, _ =>
            {
                commitCalled = true;
                return Task.CompletedTask;
            }, deadline.Token);
        });

        sender = new TcpClient { NoDelay = true };
        await sender.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        Guid id = Guid.NewGuid();
        await drive(sender.GetStream(), id, deadline.Token);
        sender.Dispose();
        sender = null;

        Require(await ExpectFailureAsync(receiverTask));
        Require(!commitCalled);
        Require(!HasTransientStreamArtifacts(root));
    }
    finally
    {
        sender?.Dispose();
        deadline.Cancel();
        if (receiverTask is not null)
        {
            try
            {
                await receiverTask;
            }
            catch
            {
                // The negative case intentionally rejects the stream.
            }
        }

        listener?.Stop();
        DeleteTestDirectory(root);
    }
}

static async Task RunCancelledReceiverAsync(byte[] key, StreamManifest manifest)
{
    string root = MakeTempDirectory();
    TcpListener? listener = null;
    TcpClient? sender = null;
    Task? receiverTask = null;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    using var receiveCancellation = new CancellationTokenSource();
    bool commitCalled = false;
    try
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        receiverTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await StreamTransfer.ReceiveAsync(accepted, key, root, _ =>
            {
                commitCalled = true;
                return Task.CompletedTask;
            }, receiveCancellation.Token);
        });

        sender = new TcpClient { NoDelay = true };
        await sender.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        await SendOfferAndReadyAsync(sender.GetStream(), Guid.NewGuid(), manifest, key, deadline.Token);
        receiveCancellation.Cancel();

        Require(await ExpectFailureAsync(receiverTask));
        Require(!commitCalled);
        Require(!HasTransientStreamArtifacts(root));
    }
    finally
    {
        sender?.Dispose();
        receiveCancellation.Cancel();
        deadline.Cancel();
        if (receiverTask is not null)
        {
            try
            {
                await receiverTask;
            }
            catch
            {
                // Cancellation is the expected negative result.
            }
        }

        listener?.Stop();
        DeleteTestDirectory(root);
    }
}

static async Task RunValidTextCleanupAsync(byte[] key)
{
    string root = MakeTempDirectory();
    TcpListener? listener = null;
    TcpClient? sender = null;
    Task? receiverTask = null;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    string text = "synthetic text α 😀";
    byte[] textBytes = Encoding.UTF8.GetBytes(text);
    StreamManifest manifest = new("text", new[] { new StreamItem("clipboard.txt", (ulong)textBytes.Length) }, (ulong)textBytes.Length);
    bool commitCalled = false;
    try
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        receiverTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await StreamTransfer.ReceiveAsync(accepted, key, root, async received =>
            {
                Require(received.Manifest.Type == "text");
                Require(received.Paths.Count == 1);
                Require(File.Exists(received.Paths[0]));
                Require((await File.ReadAllBytesAsync(received.Paths[0])).AsSpan().SequenceEqual(textBytes));
                commitCalled = true;
            }, deadline.Token);
        });

        sender = new TcpClient { NoDelay = true };
        await sender.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        NetworkStream network = sender.GetStream();
        Guid id = Guid.NewGuid();
        await SendOfferAndReadyAsync(network, id, manifest, key, deadline.Token);
        await SendChunkAndReadAckAsync(network, id, 1, 0, textBytes, key, deadline.Token);
        await SendRecordAsync(network, new StreamRecord(id, 2, StreamRecordKind.Finish, SHA256.HashData(textBytes)), key, deadline.Token);
        StreamRecord done = await ReceiveRecordAsync(network, key, deadline.Token);
        Require(done.Id == id && done.Sequence == 2 && done.Kind == StreamRecordKind.Done && done.Body.Length == 0);
        await receiverTask;
        Require(commitCalled);
        Require(!HasTransientStreamArtifacts(root));
    }
    finally
    {
        sender?.Dispose();
        deadline.Cancel();
        if (receiverTask is not null)
        {
            try
            {
                await receiverTask;
            }
            catch
            {
                // The assertion in the main body reports an unexpected failure.
            }
        }

        listener?.Stop();
        DeleteTestDirectory(root);
    }
}

static async Task RunOversizeSourceRejectionAsync(byte[] key)
{
    string root = MakeTempDirectory();
    try
    {
        string path = Path.Combine(root, "oversize.bin");
        await using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
        {
            file.SetLength(checked((long)StreamProtocol.MaxFileBytes + 1));
        }

        Require(new FileInfo(path).Length == checked((long)StreamProtocol.MaxFileBytes + 1));
        bool rejected = false;
        try
        {
            await StreamTransfer.SendFilesAsync("127.0.0.1", 0, key, new[] { path });
        }
        catch
        {
            rejected = true;
        }

        Require(rejected);
    }
    finally
    {
        DeleteTestDirectory(root);
    }
}

static async Task RunCacheBudgetRejectionAsync(byte[] key)
{
    string root = MakeTempDirectory();
    TcpListener? listener = null;
    TcpClient? sender = null;
    Task? receiverTask = null;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    bool commitCalled = false;
    try
    {
        string existing = Path.Combine(root, ".lcs2-complete-existing");
        Directory.CreateDirectory(existing);
        string existingFile = Path.Combine(existing, "synthetic.bin");
        await using (FileStream file = new(existingFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
        {
            file.SetLength(checked((long)StreamTransfer.CacheBudgetBytes));
        }

        StreamManifest manifest = new("files", new[] { new StreamItem("payload.bin", 1) }, 1);
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        receiverTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await StreamTransfer.ReceiveAsync(accepted, key, root, _ =>
            {
                commitCalled = true;
                return Task.CompletedTask;
            }, deadline.Token);
        });

        sender = new TcpClient { NoDelay = true };
        await sender.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        NetworkStream network = sender.GetStream();
        Guid id = Guid.NewGuid();
        await SendRecordAsync(network, new StreamRecord(id, 0, StreamRecordKind.Offer, StreamProtocol.EncodeManifest(manifest)), key, deadline.Token);

        Require(await ExpectFailureAsync(receiverTask));
        using var noReady = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        byte[] firstByte = new byte[1];
        bool closedWithoutReady = false;
        try
        {
            closedWithoutReady = await network.ReadAsync(firstByte, noReady.Token) == 0;
        }
        catch (OperationCanceledException)
        {
            closedWithoutReady = false;
        }

        Require(closedWithoutReady);
        Require(!commitCalled);
        Require(!Directory.EnumerateDirectories(root, ".lcs2-temp-*", SearchOption.TopDirectoryOnly).Any());
    }
    finally
    {
        sender?.Dispose();
        deadline.Cancel();
        if (receiverTask is not null)
        {
            try
            {
                await receiverTask;
            }
            catch
            {
                // Cache exhaustion is the expected negative result.
            }
        }

        listener?.Stop();
        DeleteTestDirectory(root);
    }
}

static async Task SendOfferAndReadyAsync(NetworkStream network, Guid id, StreamManifest manifest, byte[] key, CancellationToken cancellationToken)
{
    await SendRecordAsync(network, new StreamRecord(id, 0, StreamRecordKind.Offer, StreamProtocol.EncodeManifest(manifest)), key, cancellationToken);
    StreamRecord ready = await ReceiveRecordAsync(network, key, cancellationToken);
    Require(ready.Id == id && ready.Sequence == 0 && ready.Kind == StreamRecordKind.Ready && ready.Body.Length == 0);
}

static async Task SendChunkAndReadAckAsync(NetworkStream network, Guid id, uint sequence, ulong offset, byte[] payload, byte[] key, CancellationToken cancellationToken)
{
    await SendRecordAsync(network, new StreamRecord(id, sequence, StreamRecordKind.Chunk, ChunkBody(0, offset, payload)), key, cancellationToken);
    StreamRecord ack = await ReceiveRecordAsync(network, key, cancellationToken);
    Require(ack.Id == id && ack.Sequence == sequence && ack.Kind == StreamRecordKind.ChunkAck && ack.Body.Length == 0);
}

static byte[] ChunkBody(uint itemIndex, ulong offset, ReadOnlySpan<byte> payload)
{
    byte[] body = new byte[StreamProtocol.ChunkMetadataBytes + payload.Length];
    BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), itemIndex);
    BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(4, 8), offset);
    payload.CopyTo(body.AsSpan(StreamProtocol.ChunkMetadataBytes));
    return body;
}

static async Task SendRecordAsync(NetworkStream network, StreamRecord record, byte[] key, CancellationToken cancellationToken)
{
    byte[] frame = StreamProtocol.EncodeFrame(record, key);
    await network.WriteAsync(frame, cancellationToken);
    await network.FlushAsync(cancellationToken);
}

static async Task<StreamRecord> ReceiveRecordAsync(NetworkStream network, byte[] key, CancellationToken cancellationToken)
{
    byte[] header = new byte[4];
    await ReadExactlyAsync(network, header, cancellationToken);
    uint envelopeLength = BinaryPrimitives.ReadUInt32BigEndian(header);
    Require(envelopeLength >= StreamProtocol.MinEnvelopeBytes && envelopeLength <= StreamProtocol.MaxEnvelopeBytes);
    byte[] envelope = new byte[envelopeLength];
    await ReadExactlyAsync(network, envelope, cancellationToken);
    return StreamProtocol.DecodeEnvelope(envelope, key);
}

static async Task ReadExactlyAsync(NetworkStream network, Memory<byte> destination, CancellationToken cancellationToken)
{
    int offset = 0;
    while (offset < destination.Length)
    {
        int count = await network.ReadAsync(destination[offset..], cancellationToken);
        if (count == 0)
        {
            throw new InvalidOperationException();
        }

        offset += count;
    }
}

static async Task<bool> ExpectFailureAsync(Task task)
{
    try
    {
        await task;
        return false;
    }
    catch
    {
        return true;
    }
}

static bool HasTransientStreamArtifacts(string root)
{
    return Directory.EnumerateFileSystemEntries(root, ".lcs2-*", SearchOption.TopDirectoryOnly).Any();
}

static void DeleteTestDirectory(string root)
{
    try
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
    catch
    {
        // Test cleanup must not expose local paths in output.
    }
}

static async Task VerifyReceivedAsync(ReceivedStream received, int expectedPayloadLength)
{
    Require(received.Manifest.Type == "files");
    Require(received.Manifest.Items.Count == 2);
    Require(received.Manifest.Items[0] == new StreamItem("payload.bin", (ulong)expectedPayloadLength));
    Require(received.Manifest.Items[1] == new StreamItem("empty.txt", 0));
    Require(received.Paths.Count == 2);
    Require(await VerifyFixtureAsync(received.Paths[0], expectedPayloadLength));
    Require(new FileInfo(received.Paths[1]).Length == 0);
}

static async Task<bool> VerifyFixtureAsync(string path, int expectedLength)
{
    await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
    byte[] buffer = new byte[64 * 1024];
    int index = 0;
    while (index < expectedLength)
    {
        int want = Math.Min(buffer.Length, expectedLength - index);
        int read = await stream.ReadAsync(buffer.AsMemory(0, want));
        if (read == 0)
        {
            return false;
        }

        for (int i = 0; i < read; i++)
        {
            if (buffer[i] != (byte)((index + i) % 251))
            {
                return false;
            }
        }

        index += read;
    }

    return await stream.ReadAsync(buffer.AsMemory(0, 1)) == 0;
}

static async Task WriteFixtureAsync(string path, int length)
{
    await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
    byte[] buffer = new byte[64 * 1024];
    int offset = 0;
    while (offset < length)
    {
        int count = Math.Min(buffer.Length, length - offset);
        for (int i = 0; i < count; i++)
        {
            buffer[i] = (byte)((offset + i) % 251);
        }

        await stream.WriteAsync(buffer.AsMemory(0, count));
        offset += count;
    }
}

static string MakeTempDirectory()
{
    string root = Path.Combine(Path.GetTempPath(), "lcs2-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
}

static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

static void Reject(Action action)
{
    bool rejected = false;
    try
    {
        action();
    }
    catch
    {
        rejected = true;
    }

    Require(rejected);
}

static void Require(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException();
    }
}
