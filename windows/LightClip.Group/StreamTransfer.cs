using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LightClip.Windows;

/// <summary>Raised when a stream cannot complete without exposing source data.</summary>
public sealed class StreamTransferException : Exception
{
    public StreamTransferException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Async STREAM_V2 sender and receiver. It deliberately has no clipboard or
/// group-key lookup code; callers provide the already authenticated 32-byte key
/// and the commit callback that owns the platform clipboard operation.
/// </summary>
public static class StreamTransfer
{
    public const ulong CacheBudgetBytes = 10UL * 1024UL * 1024UL * 1024UL;
    public const int MaxInboundStreams = 2;
    public const int MaxOutboundStreams = 2;

    private static readonly object CacheGate = new();
    private static ulong ReservedCacheBytes;
    private static readonly ReplayWindow IncomingReplay = new();

    /// <summary>Streams one to thirty-two ordinary files to a peer.</summary>
    public static Task SendFilesAsync(
        string host,
        int port,
        ReadOnlyMemory<byte> key,
        IReadOnlyList<string> paths,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(paths);
        StreamProtocol.ValidateKey(key.Span);
        if (paths.Count == 0 || paths.Count > StreamProtocol.MaxFileItems)
        {
            throw new StreamProtocolException("File item count is invalid.");
        }

        var sources = new List<Source>(paths.Count);
        foreach (string? path in paths)
        {
            if (path is null)
            {
                throw new StreamTransferException("Source file is invalid.");
            }

            sources.Add(Source.FromFile(path));
        }

        ulong total = CheckedTotal(sources);
        var manifest = new StreamManifest("files", sources.Select(source => new StreamItem(source.Name, source.Length)).ToArray(), total);
        return SendSourcesAsync(host, port, key, manifest, sources, progress, cancellationToken);
    }

    /// <summary>Call only at startup, before accepting new stream connections.</summary>
    public static void CleanupAbandonedTransfers(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot) || (File.GetAttributes(cacheRoot) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string directory in Directory.EnumerateDirectories(cacheRoot, ".lcs2-temp-*"))
        {
            string name = Path.GetFileName(directory);
            if (Guid.TryParseExact(name[11..], "N", out _) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                DeletePrivateDirectory(directory);
        }
    }

    /// <summary>Streams strict UTF-8 text using the fixed clipboard.txt item name.</summary>
    public static Task SendTextAsync(
        string host,
        int port,
        ReadOnlyMemory<byte> key,
        string text,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(text);
        StreamProtocol.ValidateKey(key.Span);

        byte[] bytes;
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            if (utf8.GetByteCount(text) > StreamProtocol.MaxTextBytes)
                throw new StreamProtocolException("Text is too large.");
            bytes = utf8.GetBytes(text);
        }
        catch (EncoderFallbackException ex)
        {
            throw new StreamProtocolException("Text is not valid UTF-8.", ex);
        }

        if (bytes.Length > StreamProtocol.MaxTextBytes)
        {
            throw new StreamProtocolException("Text is too large.");
        }

        var source = Source.FromMemory("clipboard.txt", bytes);
        var manifest = new StreamManifest("text", new[] { new StreamItem("clipboard.txt", (ulong)bytes.Length) }, (ulong)bytes.Length);
        return SendSourcesAsync(host, port, key, manifest, new[] { source }, progress, cancellationToken);
    }

    /// <summary>
    /// Receives one offer and commits its private, atomically renamed directory
    /// through <paramref name="commit"/>. The callback must complete successfully
    /// before the protocol's done record is sent.
    /// </summary>
    public static async Task ReceiveAsync(
        TcpClient client,
        ReadOnlyMemory<byte> key,
        string cacheRoot,
        Func<ReceivedStream, Task> commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(cacheRoot);
        ArgumentNullException.ThrowIfNull(commit);
        StreamProtocol.ValidateKey(key.Span);

        var clock = new TransferClock(cancellationToken);
        Reservation? reservation = null;
        string? temporaryDirectory = null;
        string? completedDirectory = null;
        bool committed = false;
        bool textTransfer = false;
        FileStream?[] files = Array.Empty<FileStream?>();
        IncrementalHash?[] hashes = Array.Empty<IncrementalHash?>();

        try
        {
            using NetworkStream network = client.GetStream();
            StreamRecord offer = await ReadFrameAsync(network, key, clock, firstAuthenticationFrame: true).ConfigureAwait(false);
            RequireRecord(offer, offer.Id, 0, StreamRecordKind.Offer);
            StreamManifest manifest = StreamProtocol.DecodeManifest(offer.Body);
            textTransfer = manifest.Type == "text";
            if (!IncomingReplay.TryAdd(offer.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                throw new StreamProtocolException("Stream replay was rejected.");
            }

            reservation = ReserveCache(cacheRoot, manifest.Total);
            temporaryDirectory = CreatePrivateDirectory(cacheRoot, Guid.NewGuid(), out string root);
            files = new FileStream[manifest.Items.Count];
            hashes = new IncrementalHash[manifest.Items.Count];
            string[] paths = new string[manifest.Items.Count];
            for (int index = 0; index < manifest.Items.Count; index++)
            {
                StreamItem item = manifest.Items[index];
                string path = Path.Combine(temporaryDirectory, item.Name);
                paths[index] = path;
                try
                {
                    files[index] = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    EnsurePrivateFile(path);
                    hashes[index] = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                }
                catch
                {
                    throw new StreamTransferException("Receiving file could not be created.");
                }
            }

            await WriteFrameAsync(network, new StreamRecord(offer.Id, 0, StreamRecordKind.Ready, ReadOnlySpan<byte>.Empty), key, clock, firstAuthenticationFrame: true).ConfigureAwait(false);
            clock.MarkAuthenticated();
            int currentItem = 0;
            ulong currentOffset = 0;
            while (currentItem < manifest.Items.Count && manifest.Items[currentItem].Size == 0)
            {
                currentItem++;
            }

            uint expectedSequence = 1;
            Utf8Accumulator? textAccumulator = manifest.Type == "text" ? new Utf8Accumulator() : null;

            while (true)
            {
                StreamRecord record = await ReadFrameAsync(network, key, clock, firstAuthenticationFrame: false).ConfigureAwait(false);
                RequireRecordIdAndSequence(record, offer.Id, expectedSequence);
                if (record.Kind == StreamRecordKind.Chunk)
                {
                    if (currentItem >= manifest.Items.Count || record.Body.Length < StreamProtocol.ChunkMetadataBytes + 1)
                    {
                        throw new StreamProtocolException("Chunk order is invalid.");
                    }

                    uint itemIndex = BinaryPrimitives.ReadUInt32BigEndian(record.Body.AsSpan(0, 4));
                    ulong offset = BinaryPrimitives.ReadUInt64BigEndian(record.Body.AsSpan(4, 8));
                    StreamItem item = manifest.Items[currentItem];
                    int payloadLength = record.Body.Length - StreamProtocol.ChunkMetadataBytes;
                    if (itemIndex != (uint)currentItem || offset != currentOffset || (ulong)payloadLength > item.Size - currentOffset)
                    {
                        throw new StreamProtocolException("Chunk offset is invalid.");
                    }

                    ReadOnlyMemory<byte> payload = record.Body.AsMemory(StreamProtocol.ChunkMetadataBytes, payloadLength);
                    try
                    {
                        using (CancellationTokenSource writeToken = clock.CreateIoToken(firstAuthenticationFrame: false))
                        {
                            await files[currentItem]!.WriteAsync(payload, writeToken.Token).ConfigureAwait(false);
                        }

                        using (CancellationTokenSource flushToken = clock.CreateIoToken(firstAuthenticationFrame: false))
                        {
                            await files[currentItem]!.FlushAsync(flushToken.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new StreamTransferException("Stream timed out.");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        throw new StreamTransferException("Receiving file could not be written.");
                    }

                    try
                    {
                        hashes[currentItem]!.AppendData(payload.Span);
                        textAccumulator?.Append(payload.Span);
                    }
                    catch (DecoderFallbackException ex)
                    {
                        throw new StreamProtocolException("Text is not valid UTF-8.", ex);
                    }

                    currentOffset = checked(currentOffset + (ulong)payloadLength);
                    if (currentOffset == item.Size)
                    {
                        currentItem++;
                        currentOffset = 0;
                        while (currentItem < manifest.Items.Count && manifest.Items[currentItem].Size == 0)
                        {
                            currentItem++;
                        }
                    }

                    await WriteFrameAsync(network, new StreamRecord(offer.Id, expectedSequence, StreamRecordKind.ChunkAck, ReadOnlySpan<byte>.Empty), key, clock, firstAuthenticationFrame: false).ConfigureAwait(false);
                    expectedSequence++;
                    continue;
                }

                if (record.Kind != StreamRecordKind.Finish)
                {
                    throw new StreamProtocolException("Unexpected stream record.");
                }

                if (currentItem != manifest.Items.Count || currentOffset != 0 || record.Body.Length != manifest.Items.Count * SHA256.HashSizeInBytes)
                {
                    throw new StreamProtocolException("Stream is incomplete.");
                }

                textAccumulator?.Complete();
                byte[] expectedHash = new byte[SHA256.HashSizeInBytes];
                for (int index = 0; index < manifest.Items.Count; index++)
                {
                    try
                    {
                        using (CancellationTokenSource flushToken = clock.CreateIoToken(firstAuthenticationFrame: false))
                        {
                            await files[index]!.FlushAsync(flushToken.Token).ConfigureAwait(false);
                        }

                        if (files[index]!.Length != checked((long)manifest.Items[index].Size))
                        {
                            throw new StreamProtocolException("Received file size is invalid.");
                        }

                        byte[] actualHash = hashes[index]!.GetHashAndReset();
                        record.Body.AsSpan(index * SHA256.HashSizeInBytes, SHA256.HashSizeInBytes).CopyTo(expectedHash);
                        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                        {
                            throw new StreamProtocolException("Received file hash is invalid.");
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new StreamTransferException("Stream timed out.");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(expectedHash);
                    }
                }

                foreach (FileStream? file in files)
                {
                    file?.Dispose();
                }

                for (int index = 0; index < paths.Length; index++)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(paths[index]);
                        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || new FileInfo(paths[index]).Length != checked((long)manifest.Items[index].Size))
                        {
                            throw new StreamProtocolException("Receiving file is invalid.");
                        }
                    }
                    catch (StreamProtocolException)
                    {
                        throw;
                    }
                    catch
                    {
                        throw new StreamTransferException("Receiving file is invalid.");
                    }
                }

                string finalName = ".lcs2-complete-" + Guid.NewGuid().ToString("N");
                completedDirectory = Path.Combine(root, finalName);
                try
                {
                    Directory.Move(temporaryDirectory, completedDirectory);
                }
                catch
                {
                    throw new StreamTransferException("Receiving directory could not be committed.");
                }

                temporaryDirectory = null;
                string[] committedPaths = manifest.Items.Select(item => Path.Combine(completedDirectory, item.Name)).ToArray();
                try
                {
                    await commit(new ReceivedStream(manifest, committedPaths)).ConfigureAwait(false);
                    committed = true;
                }
                catch
                {
                    throw new StreamTransferException("Receiving commit was rejected.");
                }

                await WriteFrameAsync(network, new StreamRecord(offer.Id, expectedSequence, StreamRecordKind.Done, ReadOnlySpan<byte>.Empty), key, clock, firstAuthenticationFrame: false).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StreamProtocolException)
        {
            throw;
        }
        catch (StreamTransferException)
        {
            throw;
        }
        catch
        {
            throw new StreamTransferException("Stream transfer failed.");
        }
        finally
        {
            foreach (FileStream? file in files)
            {
                file?.Dispose();
            }

            foreach (IncrementalHash? hash in hashes)
            {
                hash?.Dispose();
            }

            if (!committed || textTransfer)
            {
                DeletePrivateDirectory(temporaryDirectory);
                DeletePrivateDirectory(completedDirectory);
            }

            reservation?.Dispose();
        }
    }

    private static async Task SendSourcesAsync(
        string host,
        int port,
        ReadOnlyMemory<byte> key,
        StreamManifest manifest,
        IReadOnlyList<Source> sources,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        StreamProtocol.ValidateManifest(manifest);
        var clock = new TransferClock(cancellationToken);
        try
        {
            using TcpClient client = new();
            client.NoDelay = true;
            using (CancellationTokenSource connectToken = clock.CreateIoToken(firstAuthenticationFrame: true))
            {
                await client.ConnectAsync(host, port, connectToken.Token).ConfigureAwait(false);
            }

            using NetworkStream network = client.GetStream();
            Guid id = Guid.NewGuid();
            byte[] offerBody = StreamProtocol.EncodeManifest(manifest);
            await WriteFrameAsync(network, new StreamRecord(id, 0, StreamRecordKind.Offer, offerBody), key, clock, firstAuthenticationFrame: true).ConfigureAwait(false);
            StreamRecord ready = await ReadFrameAsync(network, key, clock, firstAuthenticationFrame: true).ConfigureAwait(false);
            RequireRecord(ready, id, 0, StreamRecordKind.Ready);

            clock.MarkAuthenticated();
            ulong sentBytes = 0;
            var hashes = new List<byte[]>(sources.Count);
            byte[] chunk = new byte[StreamProtocol.ChunkBytes];
            uint sequence = 1;
            for (int itemIndex = 0; itemIndex < sources.Count; itemIndex++)
            {
                Source source = sources[itemIndex];
                source.VerifyStable();
                using Stream input = source.Open();
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                ulong offset = 0;
                while (offset < source.Length)
                {
                    int requested = checked((int)Math.Min((ulong)chunk.Length, source.Length - offset));
                    int read = 0;
                    while (read < requested)
                    {
                        int count;
                        try
                        {
                            using CancellationTokenSource readToken = clock.CreateIoToken(firstAuthenticationFrame: false);
                            count = await input.ReadAsync(chunk.AsMemory(read, requested - read), readToken.Token).ConfigureAwait(false);
                            if (count == 0)
                            {
                                throw new StreamTransferException("Source file changed during transfer.");
                            }
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new StreamTransferException("Stream timed out.");
                        }

                        read += count;
                    }

                    hash.AppendData(chunk.AsSpan(0, read));
                    byte[] body = new byte[StreamProtocol.ChunkMetadataBytes + read];
                    BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), checked((uint)itemIndex));
                    BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(4, 8), offset);
                    chunk.AsSpan(0, read).CopyTo(body.AsSpan(StreamProtocol.ChunkMetadataBytes));
                    await WriteFrameAsync(network, new StreamRecord(id, sequence, StreamRecordKind.Chunk, body), key, clock, firstAuthenticationFrame: false).ConfigureAwait(false);
                    StreamRecord ack = await ReadFrameAsync(network, key, clock, firstAuthenticationFrame: false).ConfigureAwait(false);
                    RequireRecord(ack, id, sequence, StreamRecordKind.ChunkAck);
                    offset = checked(offset + (ulong)read);
                    sentBytes = checked(sentBytes + (ulong)read);
                    progress?.Report(manifest.Total == 0 ? 1.0 : (double)sentBytes / manifest.Total);
                    sequence++;
                }

                source.VerifyStable();
                hashes.Add(hash.GetHashAndReset());
            }

            foreach (Source source in sources)
            {
                source.VerifyStable();
            }

            byte[] finishBody = new byte[hashes.Count * SHA256.HashSizeInBytes];
            for (int index = 0; index < hashes.Count; index++)
            {
                hashes[index].AsSpan().CopyTo(finishBody.AsSpan(index * SHA256.HashSizeInBytes, SHA256.HashSizeInBytes));
            }

            uint finishSequence = sequence;
            await WriteFrameAsync(network, new StreamRecord(id, finishSequence, StreamRecordKind.Finish, finishBody), key, clock, firstAuthenticationFrame: false).ConfigureAwait(false);
            StreamRecord done = await ReadFrameAsync(network, key, clock, firstAuthenticationFrame: false).ConfigureAwait(false);
            RequireRecord(done, id, finishSequence, StreamRecordKind.Done);
            foreach (Source source in sources)
            {
                source.VerifyStable();
            }

            progress?.Report(1.0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StreamProtocolException)
        {
            throw;
        }
        catch (StreamTransferException)
        {
            throw;
        }
        catch
        {
            throw new StreamTransferException("Stream transfer failed.");
        }
        finally
        {
            foreach (Source source in sources)
            {
                source.DisposeMemory();
            }
        }
    }

    private static async Task WriteFrameAsync(NetworkStream network, StreamRecord record, ReadOnlyMemory<byte> key, TransferClock clock, bool firstAuthenticationFrame)
    {
        byte[] frame = StreamProtocol.EncodeFrame(record, key.Span);
        int offset = 0;
        while (offset < frame.Length)
        {
            try
            {
                using CancellationTokenSource writeToken = clock.CreateIoToken(firstAuthenticationFrame);
                await network.WriteAsync(frame.AsMemory(offset), writeToken.Token).ConfigureAwait(false);
                clock.NoteProgress();
                offset = frame.Length;
            }
            catch (OperationCanceledException) when (!clock.ExternalCancellationRequested)
            {
                throw new StreamTransferException("Stream timed out.");
            }
        }
    }

    private static async Task<StreamRecord> ReadFrameAsync(NetworkStream network, ReadOnlyMemory<byte> key, TransferClock clock, bool firstAuthenticationFrame)
    {
        byte[] header = new byte[4];
        await ReadExactlyAsync(network, header, clock, firstAuthenticationFrame).ConfigureAwait(false);
        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (declaredLength < StreamProtocol.MinEnvelopeBytes || declaredLength > (firstAuthenticationFrame ? StreamProtocol.MinEnvelopeBytes + StreamProtocol.MaxManifestBytes : StreamProtocol.MaxEnvelopeBytes))
        {
            throw new StreamProtocolException("Declared envelope length is invalid.");
        }

        byte[] envelope = new byte[declaredLength];
        await ReadExactlyAsync(network, envelope, clock, firstAuthenticationFrame).ConfigureAwait(false);
        return StreamProtocol.DecodeEnvelope(envelope, key.Span);
    }

    private static async Task ReadExactlyAsync(NetworkStream network, Memory<byte> destination, TransferClock clock, bool firstAuthenticationFrame)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int count;
            try
            {
                using CancellationTokenSource readToken = clock.CreateIoToken(firstAuthenticationFrame);
                count = await network.ReadAsync(destination[offset..], readToken.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!clock.ExternalCancellationRequested)
            {
                throw new StreamTransferException("Stream timed out.");
            }

            if (count == 0)
            {
                throw new StreamTransferException("Peer closed the stream.");
            }

            offset += count;
            clock.NoteProgress();
        }
    }

    private static void RequireRecord(StreamRecord record, Guid id, uint sequence, byte kind)
    {
        if (record.Id != id || record.Sequence != sequence || record.Kind != kind || record.Body.Length != 0 && kind != StreamRecordKind.Offer)
        {
            throw new StreamProtocolException("Stream acknowledgement is invalid.");
        }
    }

    private static void RequireRecordIdAndSequence(StreamRecord record, Guid id, uint sequence)
    {
        if (record.Id != id || record.Sequence != sequence)
        {
            throw new StreamProtocolException("Stream sequence is invalid.");
        }
    }

    private static ulong CheckedTotal(IReadOnlyList<Source> sources)
    {
        ulong total = 0;
        foreach (Source source in sources)
        {
            try
            {
                total = checked(total + source.Length);
            }
            catch (OverflowException ex)
            {
                throw new StreamProtocolException("File total overflows.", ex);
            }
        }

        if (total > StreamProtocol.MaxFileBytes)
        {
            throw new StreamProtocolException("File total is too large.");
        }

        return total;
    }

    private static Reservation ReserveCache(string cacheRoot, ulong incomingBytes)
    {
        if (incomingBytes > CacheBudgetBytes)
        {
            throw new StreamProtocolException("Receiving cache budget is exceeded.");
        }

        string root;
        try
        {
            root = Path.GetFullPath(cacheRoot);
            if (IsUncPath(root))
            {
                throw new StreamProtocolException("Receiving cache path is invalid.");
            }

            Directory.CreateDirectory(root);
            EnsurePrivateDirectory(root);
        }
        catch (StreamProtocolException)
        {
            throw;
        }
        catch
        {
            throw new StreamTransferException("Receiving cache is unavailable.");
        }

        lock (CacheGate)
        {
            ulong used = ScanCacheBytes(root);
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(root)!);
                if (!drive.IsReady || (ulong)Math.Max(0, drive.AvailableFreeSpace) < incomingBytes + 64UL * 1024 * 1024)
                    throw new StreamTransferException("Insufficient receiving disk space.");
                ulong combined = checked(used + ReservedCacheBytes + incomingBytes);
                if (combined > CacheBudgetBytes)
                {
                    throw new StreamProtocolException("Receiving cache budget is exceeded.");
                }

                ReservedCacheBytes = checked(ReservedCacheBytes + incomingBytes);
                return new Reservation(incomingBytes);
            }
            catch (OverflowException ex)
            {
                throw new StreamProtocolException("Receiving cache budget is exceeded.", ex);
            }
        }
    }

    private static ulong ScanCacheBytes(string root)
    {
        ulong total = 0;
        try
        {
            var pending = new Stack<string>();
            foreach (string completed in Directory.EnumerateDirectories(root, ".lcs2-complete-*"))
            {
                if ((File.GetAttributes(completed) & FileAttributes.ReparsePoint) == 0) pending.Push(completed);
            }
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                    else
                    {
                        long length = new FileInfo(entry).Length;
                        if (length < 0)
                        {
                            throw new IOException();
                        }

                        total = checked(total + (ulong)length);
                    }
                }
            }
        }
        catch (OverflowException ex)
        {
            throw new StreamProtocolException("Receiving cache budget is exceeded.", ex);
        }
        catch
        {
            throw new StreamTransferException("Receiving cache is unavailable.");
        }

        return total;
    }

    private static string CreatePrivateDirectory(string cacheRoot, Guid id, out string root)
    {
        try
        {
            root = Path.GetFullPath(cacheRoot);
            EnsurePrivateDirectory(root);
            string directory = Path.Combine(root, ".lcs2-temp-" + id.ToString("N"));
            Directory.CreateDirectory(directory);
            EnsurePrivateDirectory(directory);
            return directory;
        }
        catch (StreamProtocolException)
        {
            throw;
        }
        catch
        {
            throw new StreamTransferException("Receiving directory could not be created.");
        }
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        FileAttributes attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new StreamProtocolException("Receiving directory is invalid.");
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            try
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (PlatformNotSupportedException)
            {
                // Filesystems without Unix modes keep their platform permissions.
            }
            catch (NotSupportedException)
            {
                // Filesystems without Unix modes keep their platform permissions.
            }
            catch (IOException)
            {
                // A Unix volume may reject mode changes while still allowing the transfer.
            }
        }
    }

    private static void EnsurePrivateFile(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (PlatformNotSupportedException)
            {
                // Filesystems without Unix modes keep the private parent directory.
            }
            catch (NotSupportedException)
            {
                // Filesystems without Unix modes keep the private parent directory.
            }
            catch (IOException)
            {
                // A Unix volume may reject mode changes while still allowing the transfer.
            }
        }
    }

    private static void DeletePrivateDirectory(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best effort and never exposes a path through an exception.
        }
    }

    private static bool IsUncPath(string path)
    {
        return path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
    }

    private sealed class Reservation : IDisposable
    {
        private readonly ulong _amount;
        private int _released;

        public Reservation(ulong amount)
        {
            _amount = amount;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            lock (CacheGate)
            {
                ReservedCacheBytes = ReservedCacheBytes >= _amount ? ReservedCacheBytes - _amount : 0;
            }
        }
    }

    private sealed class ReplayWindow
    {
        private const int MaximumEntries = 4096;
        private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);
        private readonly object _gate = new();
        private readonly Dictionary<Guid, DateTimeOffset> _entries = new();
        private readonly Queue<(Guid Id, DateTimeOffset Time)> _order = new();

        public bool TryAdd(Guid id, long nowUnixMilliseconds)
        {
            DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMilliseconds);
            lock (_gate)
            {
                while (_order.Count > 0 && now - _order.Peek().Time > MaximumAge)
                {
                    (Guid oldId, DateTimeOffset oldTime) = _order.Dequeue();
                    if (_entries.TryGetValue(oldId, out DateTimeOffset current) && current == oldTime)
                    {
                        _entries.Remove(oldId);
                    }
                }

                if (_entries.ContainsKey(id) || _entries.Count >= MaximumEntries)
                {
                    return false;
                }

                _entries[id] = now;
                _order.Enqueue((id, now));
                return true;
            }
        }
    }

    private sealed class Source
    {
        private readonly string? _path;
        private readonly byte[]? _memory;
        private readonly DateTime _lastWriteTimeUtc;
        private readonly DateTime _creationTimeUtc;
        private readonly FileAttributes _attributes;
        private readonly WindowsFileIdentity? _windowsIdentity;

        private Source(string name, ulong length, string? path, byte[]? memory, DateTime lastWriteTimeUtc, DateTime creationTimeUtc, FileAttributes attributes, WindowsFileIdentity? windowsIdentity)
        {
            Name = name;
            Length = length;
            _path = path;
            _memory = memory;
            _lastWriteTimeUtc = lastWriteTimeUtc;
            _creationTimeUtc = creationTimeUtc;
            _attributes = attributes;
            _windowsIdentity = windowsIdentity;
        }

        public string Name { get; }

        public ulong Length { get; }

        public static Source FromFile(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (IsUncPath(path) || IsUncPath(fullPath))
                {
                    throw new StreamTransferException("Source file is invalid.");
                }

                FileAttributes attributes = File.GetAttributes(fullPath);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new StreamTransferException("Source file is invalid.");
                }

                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length < 0 || (ulong)info.Length > StreamProtocol.MaxFileBytes)
                {
                    throw new StreamTransferException("Source file is invalid.");
                }

                string name = GetBaseName(path);
                StreamProtocol.ValidateManifest(new StreamManifest("files", new[] { new StreamItem(name, (ulong)info.Length) }, (ulong)info.Length));
                WindowsFileIdentity? identity = null;
                if (OperatingSystem.IsWindows())
                {
                    using FileStream probe = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                    identity = ReadWindowsFileIdentity(probe.SafeFileHandle);
                }

                return new Source(name, (ulong)info.Length, fullPath, null, info.LastWriteTimeUtc, info.CreationTimeUtc, attributes, identity);
            }
            catch (StreamProtocolException)
            {
                throw;
            }
            catch (StreamTransferException)
            {
                throw;
            }
            catch
            {
                throw new StreamTransferException("Source file is invalid.");
            }
        }

        public static Source FromMemory(string name, byte[] bytes)
        {
            StreamProtocol.ValidateManifest(new StreamManifest("text", new[] { new StreamItem(name, (ulong)bytes.Length) }, (ulong)bytes.Length));
            return new Source(name, (ulong)bytes.Length, null, bytes, default, default, 0, null);
        }

        public Stream Open()
        {
            if (_memory is not null)
            {
                return new MemoryStream(_memory, 0, _memory.Length, writable: false, publiclyVisible: true);
            }

            try
            {
                var file = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                try
                {
                    VerifyOpened(file);
                    return file;
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }
            catch
            {
                throw new StreamTransferException("Source file could not be opened.");
            }
        }

        public void VerifyStable()
        {
            if (_path is null)
            {
                return;
            }

            try
            {
                FileAttributes attributes = File.GetAttributes(_path);
                var info = new FileInfo(_path);
                if (!info.Exists || (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || (ulong)info.Length != Length || info.LastWriteTimeUtc != _lastWriteTimeUtc || info.CreationTimeUtc != _creationTimeUtc || attributes != _attributes)
                {
                    throw new StreamTransferException("Source file changed during transfer.");
                }

                if (_windowsIdentity is { } expected)
                {
                    using FileStream probe = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                    if (ReadWindowsFileIdentity(probe.SafeFileHandle) != expected)
                    {
                        throw new StreamTransferException("Source file changed during transfer.");
                    }
                }
            }
            catch (StreamTransferException)
            {
                throw;
            }
            catch
            {
                throw new StreamTransferException("Source file changed during transfer.");
            }
        }

        public void DisposeMemory()
        {
            if (_memory is not null)
            {
                CryptographicOperations.ZeroMemory(_memory);
            }
        }

        private static string GetBaseName(string path)
        {
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return slash >= 0 ? path[(slash + 1)..] : Path.GetFileName(path);
        }

        private void VerifyOpened(FileStream file)
        {
            if (_windowsIdentity is { } expected && ReadWindowsFileIdentity(file.SafeFileHandle) != expected)
            {
                throw new StreamTransferException("Source file changed during transfer.");
            }
        }

        private readonly record struct WindowsFileIdentity(uint VolumeSerial, uint IndexHigh, uint IndexLow);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle fileHandle, out ByHandleFileInformation information);

        private static WindowsFileIdentity ReadWindowsFileIdentity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                throw new StreamTransferException("Source file identity is unavailable.");
            }

            return new WindowsFileIdentity(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
        }
    }

    private sealed class Utf8Accumulator
    {
        private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
        private readonly char[] _chars = new char[4096];

        public void Append(ReadOnlySpan<byte> bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                _decoder.Convert(bytes[offset..], _chars, flush: false, out int bytesUsed, out _, out _);
                if (bytesUsed == 0)
                {
                    throw new DecoderFallbackException();
                }

                offset += bytesUsed;
            }
        }

        public void Complete()
        {
            _decoder.Convert(ReadOnlySpan<byte>.Empty, _chars, flush: true, out _, out _, out bool completed);
            if (!completed)
            {
                throw new DecoderFallbackException();
            }
        }
    }

    private sealed class TransferClock
    {
        private static readonly TimeSpan FirstDeadline = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan IdleDeadline = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TotalDeadline = TimeSpan.FromHours(24);
        private readonly CancellationToken _externalToken;
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private DateTimeOffset _lastProgress = DateTimeOffset.UtcNow;

        public TransferClock(CancellationToken externalToken)
        {
            _externalToken = externalToken;
        }

        public bool ExternalCancellationRequested => _externalToken.IsCancellationRequested;

        public void MarkAuthenticated()
        {
            NoteProgress();
        }

        public void NoteProgress()
        {
            _lastProgress = DateTimeOffset.UtcNow;
        }

        public CancellationTokenSource CreateIoToken(bool firstAuthenticationFrame)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan totalRemaining = _started + TotalDeadline - now;
            TimeSpan limit = firstAuthenticationFrame ? _started + FirstDeadline - now : (_lastProgress + IdleDeadline - now);
            if (totalRemaining < limit)
            {
                limit = totalRemaining;
            }

            if (limit <= TimeSpan.Zero)
            {
                throw new StreamTransferException("Stream timed out.");
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(_externalToken);
            linked.CancelAfter(limit);
            return linked;
        }
    }
}
