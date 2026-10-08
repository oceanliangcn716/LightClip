using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace LightClip.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using Mutex singleInstance = new(false, "LightClip.Windows.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("LightClip 已经在运行。", "LightClip", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        using MainForm form = new();
        Application.Run(form);
    }
}

internal static class LightClipBranding
{
    public static Icon LoadIcon()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "LightClip.ico"),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "assets", "LightClip.ico")),
        };

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    using Icon icon = new(candidate);
                    return (Icon)icon.Clone();
                }
            }
            catch
            {
                // Keep the tray usable if an external development copy is unavailable.
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}

internal sealed record StoredConfig(string PeerAddress, bool Paused, bool RunAtLogin);

internal sealed class ConfigStore
{
    private const string FolderName = "LightClip";
    private const string ConfigFileName = "config.json";
    private const string KeyFileName = "shared-key.dpapi";
    private const string RunValueName = "LightClip.Windows";
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    private readonly string _folderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    private string ConfigPath => Path.Combine(_folderPath, ConfigFileName);

    private string KeyPath => Path.Combine(_folderPath, KeyFileName);

    public bool TryLoad(out StoredConfig? config, out byte[]? key)
    {
        config = null;
        key = null;
        try
        {
            if (!File.Exists(ConfigPath) || !File.Exists(KeyPath))
            {
                return false;
            }

            string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
            StoredConfig? loaded = JsonSerializer.Deserialize<StoredConfig>(json);
            if (loaded is null || !IPAddress.TryParse(loaded.PeerAddress, out IPAddress? address) ||
                address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            {
                return false;
            }

            byte[] protectedKey = File.ReadAllBytes(KeyPath);
            byte[] unprotectedKey = UserDpapi.Unprotect(protectedKey);
            if (unprotectedKey.Length != LightClipProtocol.AesKeyBytes)
            {
                CryptographicOperations.ZeroMemory(unprotectedKey);
                return false;
            }

            config = loaded with { PeerAddress = address.ToString() };
            key = unprotectedKey;
            CryptographicOperations.ZeroMemory(protectedKey);
            return true;
        }
        catch
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            key = null;
            config = null;
            return false;
        }
    }

    public void Save(StoredConfig config, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IPAddress.TryParse(config.PeerAddress, out IPAddress? address) ||
            address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new InvalidDataException("Peer address is invalid.");
        }

        if (key.Length != LightClipProtocol.AesKeyBytes)
        {
            throw new InvalidDataException("Shared key length is invalid.");
        }

        Directory.CreateDirectory(_folderPath);
        byte[] protectedKey = UserDpapi.Protect(key);
        string configJson = JsonSerializer.Serialize(config with { PeerAddress = address.ToString() }, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        string keyTempPath = KeyPath + ".tmp";
        string configTempPath = ConfigPath + ".tmp";
        try
        {
            File.WriteAllBytes(keyTempPath, protectedKey);
            File.Move(keyTempPath, KeyPath, overwrite: true);
            File.WriteAllText(configTempPath, configJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(configTempPath, ConfigPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
            TryDelete(keyTempPath);
            TryDelete(configTempPath);
        }
    }

    public void ConfigureRunAtLogin(bool enabled)
    {
        using RegistryKey? runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (runKey is null)
        {
            throw new InvalidOperationException("Unable to open the current-user startup key.");
        }

        if (enabled)
        {
            string executable = Application.ExecutablePath;
            runKey.SetValue(RunValueName, $"\"{executable}\"", RegistryValueKind.String);
        }
        else
        {
            runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale temporary file contains only encrypted key bytes or a non-secret config.
        }
    }
}

internal static class LightClipIo
{
    public static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int count = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            offset += count;
        }
    }
}

/// <summary>
/// Small built-in DPAPI wrapper. CryptProtectData/CryptUnprotectData default to
/// the current Windows user when no machine flag is supplied, and no key material
/// is placed in the JSON configuration file.
/// </summary>
internal static class UserDpapi
{
    private const int MaximumBlobBytes = 4096;

    public static byte[] Protect(ReadOnlySpan<byte> value)
    {
        byte[] input = value.ToArray();
        IntPtr inputPointer = IntPtr.Zero;
        try
        {
            inputPointer = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inputPointer, input.Length);
            DataBlob inputBlob = new() { cbData = input.Length, pbData = inputPointer };
            if (!CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out DataBlob outputBlob))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return CopyAndRelease(ref outputBlob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            if (inputPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputPointer);
            }
        }
    }

    public static byte[] Unprotect(ReadOnlySpan<byte> protectedValue)
    {
        byte[] input = protectedValue.ToArray();
        IntPtr inputPointer = IntPtr.Zero;
        IntPtr descriptionPointer = IntPtr.Zero;
        try
        {
            if (input.Length == 0 || input.Length > MaximumBlobBytes)
            {
                throw new CryptographicException("DPAPI blob length is invalid.");
            }

            inputPointer = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inputPointer, input.Length);
            DataBlob inputBlob = new() { cbData = input.Length, pbData = inputPointer };
            if (!CryptUnprotectData(ref inputBlob, out descriptionPointer, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out DataBlob outputBlob))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return CopyAndRelease(ref outputBlob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            if (descriptionPointer != IntPtr.Zero)
            {
                LocalFree(descriptionPointer);
            }

            if (inputPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputPointer);
            }
        }
    }

    private static byte[] CopyAndRelease(ref DataBlob blob)
    {
        if (blob.pbData == IntPtr.Zero || blob.cbData <= 0 || blob.cbData > MaximumBlobBytes)
        {
            if (blob.pbData != IntPtr.Zero)
            {
                LocalFree(blob.pbData);
            }

            throw new CryptographicException("DPAPI output length is invalid.");
        }

        byte[] output = new byte[blob.cbData];
        try
        {
            Marshal.Copy(blob.pbData, output, 0, output.Length);
            return output;
        }
        finally
        {
            byte[] zeroes = new byte[blob.cbData];
            Marshal.Copy(zeroes, 0, blob.pbData, zeroes.Length);
            LocalFree(blob.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        out IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

internal sealed class LightClipListener : IDisposable
{
    private readonly byte[] _key;
    private readonly Func<LightClipPacket, CancellationToken, Task<bool>> _applyPacket;
    private readonly LightClipReplayWindow _replayWindow;
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly CancellationTokenSource _stopSource = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _disposed;

    public LightClipListener(
        ReadOnlySpan<byte> key,
        LightClipReplayWindow replayWindow,
        Func<LightClipPacket, CancellationToken, Task<bool>> applyPacket)
    {
        if (key.Length != LightClipProtocol.AesKeyBytes)
        {
            throw new ArgumentException("Shared key length is invalid.", nameof(key));
        }

        _key = key.ToArray();
        _replayWindow = replayWindow ?? throw new ArgumentNullException(nameof(replayWindow));
        _applyPacket = applyPacket ?? throw new ArgumentNullException(nameof(applyPacket));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_listener is not null)
        {
            return;
        }

        TcpListener listener = new(IPAddress.IPv6Any, LightClipProtocol.Port);
        listener.Server.DualMode = true;
        listener.Start();
        _listener = listener;
        _acceptLoop = AcceptLoopAsync(listener, _stopSource.Token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopSource.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Closing a listener is best effort during shutdown.
        }

        _listener = null;
        CryptographicOperations.ZeroMemory(_key);
        _slots.Dispose();
        _stopSource.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                if (!_slots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                _ = ProcessClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // The tray process owns user-facing status; packet contents and exception text stay private.
        }
    }

    private async Task ProcessClientAsync(TcpClient client, CancellationToken serverCancellationToken)
    {
        using (client)
        using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken))
        {
            try
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using NetworkStream stream = client.GetStream();
                byte[] prefix = new byte[4];
                await LightClipIo.ReadExactlyAsync(stream, prefix, deadline.Token).ConfigureAwait(false);
                uint envelopeLength = BinaryPrimitives.ReadUInt32BigEndian(prefix);
                if (envelopeLength < LightClipProtocol.EnvelopeOverheadBytes ||
                    envelopeLength > LightClipProtocol.MaxEnvelopeBytes)
                {
                    return;
                }

                byte[] envelope = new byte[checked((int)envelopeLength)];
                await LightClipIo.ReadExactlyAsync(stream, envelope, deadline.Token).ConfigureAwait(false);
                LightClipPacket request = LightClipProtocol.DecodeEnvelope(
                    envelope,
                    _key,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (!LightClipProtocol.IsRequestKind(request.Kind))
                {
                    return;
                }

                long acceptedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!_replayWindow.TryAdd(request.RequestId, acceptedAt))
                {
                    return;
                }

                if (!await _applyPacket(request, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false))
                {
                    return;
                }

                LightClipPacket ack = LightClipProtocol.CreateAck(
                    request.RequestId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                byte[] ackFrame = LightClipProtocol.EncodeFrame(ack, _key);
                await stream.WriteAsync(ackFrame, deadline.Token).ConfigureAwait(false);
                await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
            }
            catch (LightClipProtocolException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch
            {
            }
            finally
            {
                try
                {
                    _slots.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}

internal static class LightClipSender
{
    public static async Task SendAsync(
        LightClipPacket packet,
        IPAddress peer,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        if (key.Length != LightClipProtocol.AesKeyBytes)
        {
            throw new InvalidDataException("Shared key length is invalid.");
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using TcpClient client = new(peer.AddressFamily);
        await client.ConnectAsync(peer, LightClipProtocol.Port, deadline.Token).ConfigureAwait(false);
        client.NoDelay = true;
        using NetworkStream stream = client.GetStream();

        byte[] frame = LightClipProtocol.EncodeFrame(packet, key.Span);
        await stream.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);

        byte[] prefix = new byte[4];
        await LightClipIo.ReadExactlyAsync(stream, prefix, deadline.Token).ConfigureAwait(false);
        uint envelopeLength = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (envelopeLength < LightClipProtocol.EnvelopeOverheadBytes ||
            envelopeLength > LightClipProtocol.MaxEnvelopeBytes)
        {
            throw new LightClipProtocolException("ACK envelope length is invalid.");
        }

        byte[] envelope = new byte[checked((int)envelopeLength)];
        await LightClipIo.ReadExactlyAsync(stream, envelope, deadline.Token).ConfigureAwait(false);
        LightClipPacket ack = LightClipProtocol.DecodeEnvelope(
            envelope,
            key.Span,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (ack.Kind != LightClipPacketKind.Ack || ack.RequestId != packet.RequestId || ack.Body.Length != 0)
        {
            throw new LightClipProtocolException("ACK does not match the request.");
        }
    }
}

internal enum ClipboardPayloadKind
{
    Text,
    Png,
    Files,
}

internal sealed class ClipboardSnapshot
{
    public ClipboardSnapshot(ClipboardPayloadKind kind, byte[] body, uint sequenceNumber)
    {
        Kind = kind;
        Body = body;
        SequenceNumber = sequenceNumber;
        Hash = SHA256.HashData(body);
    }

    public static ClipboardSnapshot Files(string[] paths, uint sequence) => new(ClipboardPayloadKind.Files, Array.Empty<byte>(), sequence) { FilePaths = paths };

    public string[] FilePaths { get; private init; } = Array.Empty<string>();

    public ClipboardPayloadKind Kind { get; }

    public byte[] Body { get; }

    public uint SequenceNumber { get; }

    public byte[] Hash { get; }
}

internal static class ClipboardBridge
{
    private const long MaxImageFileBytes = 64L * 1024 * 1024;

    private static readonly string[] SuppressedFormats =
    {
        "org.nspasteboard.TransientType",
        "org.nspasteboard.ConcealedType",
        "application/x-keepassxc-private",
    };

    public static async Task<ClipboardSnapshot?> TryReadWithRetryAsync(
        uint? expectedSequence,
        CancellationToken cancellationToken)
    {
        uint sequence = expectedSequence ?? NativeMethods.GetClipboardSequenceNumber();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetClipboardSequenceNumber() != sequence)
            {
                return null;
            }

            try
            {
                ClipboardSnapshot? snapshot = ReadOnce(sequence);
                if (snapshot is null)
                {
                    return null;
                }

                return NativeMethods.GetClipboardSequenceNumber() == sequence ? snapshot : null;
            }
            catch (ExternalException)
            {
                // Clipboard ownership is transient; retry only while the same sequence is current.
            }
            catch (InvalidOperationException)
            {
                // Clipboard can be unavailable while another process is opening it.
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
            }
        }

        return null;
    }

    public static async Task<bool> TryWriteWithRetryAsync(
        LightClipPacket packet,
        Action<uint, byte[]> recordRemoteWrite,
        CancellationToken cancellationToken)
    {
        uint initialSequence = NativeMethods.GetClipboardSequenceNumber();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetClipboardSequenceNumber() != initialSequence)
            {
                return false;
            }

            try
            {
                if (packet.Kind == LightClipPacketKind.Text)
                {
                    string text = LightClipProtocol.DecodeText(packet.Body);
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                }
                else if (packet.Kind == LightClipPacketKind.Png)
                {
                    LightClipProtocol.ValidatePng(packet.Body);
                    using MemoryStream input = new(packet.Body, writable: false);
                    using Image decoded = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
                    if ((ulong)decoded.Width * (ulong)decoded.Height > LightClipProtocol.MaxPngPixels)
                    {
                        return false;
                    }

                    using Bitmap bitmap = new(decoded);
                    Clipboard.SetImage(bitmap);
                }
                else if (packet.Kind == LightClipPacketKind.Ping)
                {
                    return true;
                }
                else
                {
                    return false;
                }

                uint resultSequence = NativeMethods.GetClipboardSequenceNumber();
                byte[] markerHash = SHA256.HashData(packet.Body);
                if (packet.Kind == LightClipPacketKind.Png)
                {
                    // SetImage may re-encode the PNG. Hash the actual clipboard
                    // representation when available; an empty hash means that
                    // this sequence is suppressed regardless of representation.
                    try
                    {
                        ClipboardSnapshot? actual = ReadOnce(resultSequence);
                        markerHash = actual?.Hash ?? Array.Empty<byte>();
                    }
                    catch
                    {
                        markerHash = Array.Empty<byte>();
                    }
                }

                recordRemoteWrite(resultSequence, markerHash);
                return true;
            }
            catch (ExternalException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
            }
        }

        return false;
    }

    // This method runs on the WinForms STA thread. Copy=true renders the
    // formats before disposing image helpers and permits pasting after exit.
    public static async Task<bool> TryWriteStreamAsync(ReceivedStream received, uint expectedSequence,
        Action<uint, byte[]> recordRemoteWrite, CancellationToken cancellationToken)
    {
        StreamProtocol.ValidateManifest(received.Manifest);
        if (received.Paths.Count != received.Manifest.Items.Count) return false;
        string? text = null;
        if (received.Manifest.Type == "text")
        {
            if (new FileInfo(received.Paths[0]).Length > StreamProtocol.MaxTextBytes) return false;
            text = new UTF8Encoding(false, true).GetString(await File.ReadAllBytesAsync(received.Paths[0], cancellationToken));
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence) return false;
            try
            {
                if (text is not null) Clipboard.SetText(text, TextDataFormat.UnicodeText);
                else
                {
                    DataObject data = new();
                    data.SetData(DataFormats.FileDrop, false, received.Paths.ToArray());
                    using MemoryStream effect = new(BitConverter.GetBytes(1), writable: false);
                    data.SetData("Preferred DropEffect", false, effect);
                    ClipboardSnapshot? image = received.Paths.Count == 1 && IsOrdinaryImageExtension(received.Paths[0])
                        ? TryReadImageFile(received.Paths[0], expectedSequence) : null;
                    using MemoryStream? png = image is null ? null : new MemoryStream(image.Body, writable: false);
                    using Image? decoded = png is null ? null : Image.FromStream(png, false, true);
                    using Bitmap? bitmap = decoded is null ? null : new Bitmap(decoded);
                    if (png is not null) { png.Position = 0; data.SetData("PNG", false, png); }
                    if (bitmap is not null) data.SetData(DataFormats.Bitmap, true, bitmap);
                    if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence) return false;
                    Clipboard.SetDataObject(data, copy: true);
                }
                recordRemoteWrite(NativeMethods.GetClipboardSequenceNumber(), Array.Empty<byte>());
                return true;
            }
            catch (ExternalException) { }
            catch (InvalidOperationException) { }
            if (attempt < 2) await Task.Delay(40, cancellationToken);
        }
        return false;
    }

    private static ClipboardSnapshot? ReadOnce(uint sequence)
    {
        IDataObject? data = Clipboard.GetDataObject();
        if (data is null || HasSuppressedFormat(data))
        {
            return null;
        }

        if (TryReadFileDrop(data, sequence, out ClipboardSnapshot? fileSnapshot))
        {
            return fileSnapshot;
        }

        // Images take precedence over UnicodeText because Explorer and image
        // editors can publish a caption or URL next to the actual pixels.
        byte[]? png = TryReadPng(data);
        if (png is not null)
        {
            return new ClipboardSnapshot(ClipboardPayloadKind.Png, png, sequence);
        }

        if (data.GetDataPresent(DataFormats.Bitmap, autoConvert: true) &&
            data.GetData(DataFormats.Bitmap, autoConvert: true) is Image image)
        {
            ClipboardSnapshot? bitmapSnapshot = TryEncodeBitmap(image, sequence);
            return bitmapSnapshot;
        }

        if (HasOrdinaryImageFormat(data))
        {
            // A present-but-invalid image must not fall through to an attached
            // caption or URL and become an unintended text transfer.
            return null;
        }

        if (data.GetDataPresent(DataFormats.UnicodeText, autoConvert: true))
        {
            object? value = data.GetData(DataFormats.UnicodeText, autoConvert: true);
            if (value is string text)
            {
                var utf8 = new UTF8Encoding(false, true);
                if (utf8.GetByteCount(text) <= StreamProtocol.MaxTextBytes)
                    return new ClipboardSnapshot(ClipboardPayloadKind.Text, utf8.GetBytes(text), sequence);
            }
        }

        return null;
    }

    private static bool TryReadFileDrop(
        IDataObject data,
        uint sequence,
        out ClipboardSnapshot? snapshot)
    {
        snapshot = null;
        bool hasFileDrop;
        try
        {
            hasFileDrop = data.GetDataPresent(DataFormats.FileDrop, autoConvert: false) ||
                data.GetDataPresent(DataFormats.FileDrop, autoConvert: true);
        }
        catch
        {
            return true;
        }

        if (!hasFileDrop)
        {
            return false;
        }

        try
        {
            object? raw = data.GetData(DataFormats.FileDrop, autoConvert: false) ??
                data.GetData(DataFormats.FileDrop, autoConvert: true);
            if (raw is not string[] paths || paths.Length is < 1 or > 32)
            {
                return true;
            }

            var items = new List<StreamItem>();
            ulong total = 0;
            foreach (string path in paths)
            {
                if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    info.Length < 0 || (ulong)info.Length > StreamProtocol.MaxFileBytes) return true;
                total = checked(total + (ulong)info.Length);
                items.Add(new StreamItem(info.Name, (ulong)info.Length));
            }
            StreamProtocol.ValidateManifest(new StreamManifest("files", items, total));
            snapshot = ClipboardSnapshot.Files(paths, sequence);
            return true;
        }
        catch
        {
            // File-drop data is untrusted UI input. Do not expose paths or let
            // an inaccessible/invalid item escape the bounded clipboard path.
            snapshot = null;
            return true;
        }
    }

    private static ClipboardSnapshot? TryReadImageFile(string path, uint sequence)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                info.Length <= 0 || info.Length > MaxImageFileBytes)
            {
                return null;
            }

            int length = checked((int)info.Length);
            byte[] source = new byte[length];
            using (FileStream input = new(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 64 * 1024,
                       options: FileOptions.SequentialScan))
            {
                if (input.Length != length)
                {
                    return null;
                }

                int offset = 0;
                while (offset < source.Length)
                {
                    int count = input.Read(source, offset, source.Length - offset);
                    if (count == 0)
                    {
                        return null;
                    }

                    offset += count;
                }

                if (input.Length != length)
                {
                    return null;
                }
            }

            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                if (source.Length <= LightClipProtocol.MaxPngBytes)
                {
                    LightClipProtocol.ValidatePng(source);
                }
                else if (!TryGetPngDimensions(source, out uint width, out uint height))
                {
                    return null;
                }

                if (!TryGetPngDimensions(source, out uint checkedWidth, out uint checkedHeight) ||
                    (ulong)checkedWidth * checkedHeight > LightClipProtocol.MaxPngPixels)
                {
                    return null;
                }
            }
            else if (!TryGetJpegDimensions(source, out uint jpegWidth, out uint jpegHeight) ||
                     (ulong)jpegWidth * jpegHeight > LightClipProtocol.MaxPngPixels)
            {
                return null;
            }

            using MemoryStream inputStream = new(source, writable: false);
            using Image image = Image.FromStream(
                inputStream,
                useEmbeddedColorManagement: false,
                validateImageData: true);
            return TryEncodeBitmap(image, sequence);
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (LightClipProtocolException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
    }

    private static bool IsOrdinaryImageExtension(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetPngDimensions(ReadOnlySpan<byte> png, out uint width, out uint height)
    {
        width = 0;
        height = 0;
        ReadOnlySpan<byte> signature = stackalloc byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        };
        if (png.Length < 33 || !png[..signature.Length].SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.Slice(8, 4)) != 13 ||
            !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        return width != 0 && height != 0;
    }

    private static bool TryGetJpegDimensions(ReadOnlySpan<byte> jpeg, out uint width, out uint height)
    {
        width = 0;
        height = 0;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return false;
        }

        int offset = 2;
        while (offset + 1 < jpeg.Length)
        {
            if (jpeg[offset++] != 0xFF)
            {
                return false;
            }

            while (offset < jpeg.Length && jpeg[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= jpeg.Length)
            {
                return false;
            }

            byte marker = jpeg[offset++];
            if (marker == 0xD9 || marker == 0xDA)
            {
                return false;
            }

            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (offset + 2 > jpeg.Length)
            {
                return false;
            }

            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > jpeg.Length)
            {
                return false;
            }

            if (IsJpegStartOfFrame(marker))
            {
                if (segmentLength < 7)
                {
                    return false;
                }

                int frame = offset + 2;
                height = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(frame + 1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(frame + 3, 2));
                return width != 0 && height != 0;
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool IsJpegStartOfFrame(byte marker)
    {
        return marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
    }

    private static byte[]? TryReadPng(IDataObject data)
    {
        foreach (string format in new[] { "PNG", "image/png", "public.png" })
        {
            if (!data.GetDataPresent(format, autoConvert: false))
            {
                continue;
            }

            object? value = data.GetData(format, autoConvert: false);
            byte[]? bytes = value switch
            {
                byte[] direct => direct.Length <= LightClipProtocol.MaxPngBytes ? direct.ToArray() : null,
                Stream stream => CopyAtMost(stream, LightClipProtocol.MaxPngBytes),
                _ => null,
            };
            if (bytes is null)
            {
                continue;
            }

            try
            {
                LightClipProtocol.ValidatePng(bytes);
                return bytes;
            }
            catch (LightClipProtocolException)
            {
            }
        }

        return null;
    }

    private static ClipboardSnapshot? TryEncodeBitmap(Image image, uint sequence)
    {
        if (image.Width <= 0 || image.Height <= 0 ||
            (ulong)image.Width * (ulong)image.Height > LightClipProtocol.MaxPngPixels)
        {
            return null;
        }

        try
        {
            using LimitedMemoryStream output = new(LightClipProtocol.MaxPngBytes);
            image.Save(output, ImageFormat.Png);
            byte[] png = output.ToArray();
            LightClipProtocol.ValidatePng(png);
            return new ClipboardSnapshot(ClipboardPayloadKind.Png, png, sequence);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (LightClipProtocolException)
        {
            return null;
        }
    }

    private static byte[]? CopyAtMost(Stream source, int maximumBytes)
    {
        using MemoryStream destination = new(Math.Min(maximumBytes, 1024 * 1024));
        byte[] buffer = new byte[64 * 1024];
        int total = 0;
        while (true)
        {
            int count = source.Read(buffer, 0, buffer.Length);
            if (count == 0)
            {
                break;
            }

            total += count;
            if (total > maximumBytes)
            {
                return null;
            }

            destination.Write(buffer, 0, count);
        }

        return destination.ToArray();
    }

    private static bool HasOrdinaryImageFormat(IDataObject data)
    {
        foreach (string format in new[] { "PNG", "image/png", "public.png" })
        {
            if (data.GetDataPresent(format, autoConvert: false))
            {
                return true;
            }
        }

        return data.GetDataPresent(DataFormats.Bitmap, autoConvert: true);
    }

    private static bool HasSuppressedFormat(IDataObject data)
    {
        foreach (string format in SuppressedFormats)
        {
            if (data.GetDataPresent(format, autoConvert: false) || data.GetDataPresent(format, autoConvert: true))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class LimitedMemoryStream : MemoryStream
    {
        private readonly int _maximumBytes;

        public LimitedMemoryStream(int maximumBytes)
            : base(Math.Min(maximumBytes, 1024 * 1024))
        {
            _maximumBytes = maximumBytes;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityFor(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityFor(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureCapacityFor(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacityFor(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void EnsureCapacityFor(int count)
        {
            if (count < 0 || Position > _maximumBytes - count)
            {
                throw new InvalidDataException("Clipboard image is too large.");
            }
        }
    }
}

internal sealed class MainForm : Form
{
    private const int WmClipboardUpdate = 0x031D;

    private readonly ConfigStore _configStore = new();
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _sendItem;
    private readonly Icon _applicationIcon;
    private readonly LightClipReplayWindow _replayWindow = new();
    private readonly object _markerGate = new();
    private StoredConfig? _config;
    private byte[]? _key;
    private LightClipListener? _listener;
    private bool _configured;
    private bool _servicesActive;
    private int _clipboardEventScheduled;
    private CancellationTokenSource? _sendCancellation;
    private long _sendGeneration;
    private TcpListener? _streamListener;
    private CancellationTokenSource? _streamStop;
    private readonly SemaphoreSlim _streamSlots = new(2, 2);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _receiveTransfers = new();
    private static readonly string TransferCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightClip", "Transfers");
    private ClipboardMarker? _remoteMarker;
    private uint? _suppressedClipboardSequence;
    private bool _settingsShown;

    public MainForm()
    {
        Text = "LightClip";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        _applicationIcon = LightClipBranding.LoadIcon();
        Icon = (Icon)_applicationIcon.Clone();

        _statusItem = new ToolStripMenuItem("状态：未启用")
        {
            Enabled = false,
        };
        _pauseItem = new ToolStripMenuItem("暂停同步");
        _pauseItem.Click += PauseItem_Click;
        ToolStripMenuItem settingsItem = new("打开设置");
        settingsItem.Click += (_, _) => OpenSettings();
        _sendItem = new ToolStripMenuItem("发送当前剪贴板");
        _sendItem.Click += (_, _) => SendCurrentClipboard();
        ToolStripMenuItem cancelItem = new("取消当前传输");
        cancelItem.Click += (_, _) => { CancelCurrentTransfers(); SetStatus("已取消当前传输"); };
        ToolStripMenuItem cacheItem = new("打开接收文件夹");
        cacheItem.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(TransferCache); Process.Start(new ProcessStartInfo(TransferCache) { UseShellExecute = true }); }
            catch { SetStatus("无法打开接收文件夹"); }
        };
        ToolStripMenuItem quitItem = new("退出");
        quitItem.Click += (_, _) => Close();

        ContextMenuStrip menu = new();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(_sendItem);
        menu.Items.Add(cancelItem);
        menu.Items.Add(cacheItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "LightClip：未启用",
            Visible = true,
            ContextMenuStrip = menu,
        };

        if (_configStore.TryLoad(out StoredConfig? storedConfig, out byte[]? storedKey))
        {
            _config = storedConfig;
            _key = storedKey;
            _configured = true;
            UpdateMenuState();
            if (_config is not null && !_config.Paused)
            {
                StartServices();
            }
            else
            {
                SetStatus("已暂停");
            }
        }
        else
        {
            SetStatus("未启用");
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Hide();
        if (!_configured && !_settingsShown)
        {
            BeginInvoke(OpenSettings);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate)
        {
            ScheduleClipboardUpdate();
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopServices();
        lock (_markerGate)
        {
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
                _key = null;
            }

            _remoteMarker = null;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _applicationIcon.Dispose();
        base.OnFormClosed(e);
    }

    internal Icon CreateApplicationIcon()
    {
        return (Icon)_applicationIcon.Clone();
    }

    private void OpenSettings()
    {
        if (_settingsShown || IsDisposed)
        {
            return;
        }

        _settingsShown = true;
        try
        {
            string? peer = _config?.PeerAddress;
            string? encodedKey = _key is null ? null : Convert.ToBase64String(_key);
            using SettingsForm dialog = new(this, peer, encodedKey, _config?.RunAtLogin ?? false);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Draft is not null)
            {
                ApplySettings(dialog.Draft);
            }
        }
        finally
        {
            _settingsShown = false;
            UpdateMenuState();
        }
    }

    private void ApplySettings(SettingsDraft draft)
    {
        try
        {
            _configStore.Save(new StoredConfig(draft.PeerAddress, Paused: false, draft.RunAtLogin), draft.SharedKey);
            _configStore.ConfigureRunAtLogin(draft.RunAtLogin);

            StopServices();
            lock (_markerGate)
            {
                if (_key is not null)
                {
                    CryptographicOperations.ZeroMemory(_key);
                }

                _key = draft.SharedKey.ToArray();
                _config = new StoredConfig(draft.PeerAddress, Paused: false, draft.RunAtLogin);
                _configured = true;
            }

            StartServices();
            if (_servicesActive)
            {
                SetStatus("已启用");
            }
        }
        catch
        {
            SetStatus("保存失败");
            MessageBox.Show("设置保存失败，请检查输入后重试。", "LightClip", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft.SharedKey);
        }
    }

    private async void PauseItem_Click(object? sender, EventArgs e)
    {
        if (!_configured || _config is null || _key is null)
        {
            return;
        }

        bool pause = !_config.Paused;
        try
        {
            _configStore.Save(_config with { Paused = pause }, _key);
            _configStore.ConfigureRunAtLogin(_config.RunAtLogin);
            _config = _config with { Paused = pause };
            if (pause)
            {
                StopServices();
                SetStatus("已暂停");
            }
            else
            {
                StartServices();
                if (_servicesActive)
                {
                    SetStatus("已启用");
                }
            }
        }
        catch
        {
            SetStatus("保存失败");
        }

        await Task.CompletedTask;
    }

    private async void SendCurrentClipboard()
    {
        if (!_servicesActive || _key is null || _config is null)
        {
            return;
        }

        CancelCurrentTransfers();

        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        try
        {
            ClipboardSnapshot? snapshot = await ClipboardBridge.TryReadWithRetryAsync(sequence, CancellationToken.None);
            if (snapshot is null)
            {
                SetStatus("没有可同步的内容");
                return;
            }

            await SendSnapshotAsync(snapshot);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            SetStatus("发送失败");
        }
    }

    private void ScheduleClipboardUpdate()
    {
        if (!_servicesActive || _settingsShown || Interlocked.Exchange(ref _clipboardEventScheduled, 1) != 0)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(async () =>
            {
                uint observed = NativeMethods.GetClipboardSequenceNumber();
                try
                {
                    await HandleClipboardUpdateAsync();
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    SetStatus("读取剪贴板失败");
                }
                finally
                {
                    Interlocked.Exchange(ref _clipboardEventScheduled, 0);
                    if (_servicesActive && NativeMethods.GetClipboardSequenceNumber() != observed) ScheduleClipboardUpdate();
                }
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _clipboardEventScheduled, 0);
        }
    }

    private async Task HandleClipboardUpdateAsync()
    {
        await Task.Yield();
        if (!_servicesActive || _key is null || _config is null)
        {
            return;
        }

        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        lock (_markerGate)
        {
            if (_suppressedClipboardSequence == sequence)
            {
                _suppressedClipboardSequence = null;
                return;
            }
        }

        if (await TryConsumeRemoteMarkerAsync(sequence))
        {
            return;
        }

        if (!_servicesActive || _key is null || _config is null)
        {
            return;
        }

        CancelCurrentTransfers();

        ClipboardSnapshot? snapshot = await ClipboardBridge.TryReadWithRetryAsync(sequence, CancellationToken.None);
        if (snapshot is null || NativeMethods.GetClipboardSequenceNumber() != sequence)
        {
            return;
        }

        _ = SendSnapshotAsync(snapshot);
    }

    private void CancelSending()
    {
        _sendGeneration++;
        _sendCancellation?.Cancel();
        _sendCancellation = null;
    }

    private void CancelCurrentTransfers()
    {
        CancelSending();
        foreach (CancellationTokenSource transfer in _receiveTransfers.Values)
        {
            try { transfer.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task SendSnapshotAsync(ClipboardSnapshot snapshot)
    {
        if (!_servicesActive || _key is null || _config is null ||
            NativeMethods.GetClipboardSequenceNumber() != snapshot.SequenceNumber ||
            !IPAddress.TryParse(_config.PeerAddress, out IPAddress? peer)) return;
        CancelSending();
        long generation = _sendGeneration;
        using CancellationTokenSource cancellation = new();
        _sendCancellation = cancellation;
        byte[] key = _key.ToArray();
        try
        {
            SetStatus("正在发送…");
            IProgress<double> progress = new Progress<double>(fraction =>
            {
                if (generation == _sendGeneration && _servicesActive) SetStatus($"正在发送文件 {fraction:P0}");
            });
            if (snapshot.Kind == ClipboardPayloadKind.Files)
                await StreamTransfer.SendFilesAsync(peer.ToString(), StreamProtocol.Port, key, snapshot.FilePaths, progress, cancellation.Token);
            else if (snapshot.Kind == ClipboardPayloadKind.Text && snapshot.Body.Length > LightClipProtocol.MaxTextBytes)
                await StreamTransfer.SendTextAsync(peer.ToString(), StreamProtocol.Port, key,
                    new UTF8Encoding(false, true).GetString(snapshot.Body), cancellationToken: cancellation.Token);
            else
            {
                LightClipPacket packet = new(Guid.NewGuid(), checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    snapshot.Kind == ClipboardPayloadKind.Text ? LightClipPacketKind.Text : LightClipPacketKind.Png, snapshot.Body);
                await LightClipSender.SendAsync(packet, peer, key, cancellation.Token);
            }
            if (generation == _sendGeneration && NativeMethods.GetClipboardSequenceNumber() == snapshot.SequenceNumber)
                SetStatus("发送成功");
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (generation == _sendGeneration && _servicesActive) SetStatus("发送失败，请确认对端已升级并在线");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (ReferenceEquals(_sendCancellation, cancellation)) _sendCancellation = null;
        }
    }

    private async Task AcceptStreamsAsync(TcpListener listener, byte[] listenerKey, CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                if (!_streamSlots.Wait(0)) { client.Dispose(); continue; }
                _ = ReceiveStreamAsync(client, listenerKey.ToArray(), NativeMethods.GetClipboardSequenceNumber(), stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally { CryptographicOperations.ZeroMemory(listenerKey); stop.Dispose(); }
    }

    private async Task ReceiveStreamAsync(TcpClient client, byte[] key, uint version, CancellationToken stop)
    {
        Guid id = Guid.NewGuid();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _receiveTransfers[id] = cancellation;
        try
        {
            using (client)
                await StreamTransfer.ReceiveAsync(client, key, TransferCache,
                    received => ApplyIncomingStreamAsync(received, version, cancellation.Token), cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { /* Failure never updates the clipboard or displays received paths. */ }
        finally
        {
            _receiveTransfers.TryRemove(id, out _);
            CryptographicOperations.ZeroMemory(key);
            _streamSlots.Release();
        }
    }

    private Task ApplyIncomingStreamAsync(ReceivedStream received, uint version, CancellationToken token)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (!_servicesActive || token.IsCancellationRequested) { completion.TrySetCanceled(); return; }
                    bool applied = await ClipboardBridge.TryWriteStreamAsync(received, version, RecordRemoteWrite, token);
                    if (!applied) { completion.TrySetException(new IOException("Clipboard changed.")); return; }
                    CancelSending();
                    SetStatus(received.Manifest.Type == "text" ? "已接收文字" : "已接收文件");
                    completion.TrySetResult();
                }
                catch { completion.TrySetException(new IOException("Clipboard commit failed.")); }
            }));
        }
        catch (InvalidOperationException) { completion.TrySetCanceled(); }
        return completion.Task.WaitAsync(token);
    }

    private Task<bool> ApplyIncomingPacketAsync(LightClipPacket packet, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested || !_servicesActive)
                    {
                        completion.TrySetResult(false);
                        return;
                    }

                    if (packet.Kind != LightClipPacketKind.Ping) CancelCurrentTransfers();
                    bool applied = await ClipboardBridge.TryWriteWithRetryAsync(
                        packet,
                        RecordRemoteWrite,
                        cancellationToken);
                    completion.TrySetResult(applied);
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetResult(false);
                }
                catch
                {
                    completion.TrySetResult(false);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private void RecordRemoteWrite(uint sequence, byte[] hash)
    {
        lock (_markerGate)
        {
            _remoteMarker = new ClipboardMarker(sequence, hash);
        }
    }

    private async Task<bool> TryConsumeRemoteMarkerAsync(uint sequence)
    {
        ClipboardMarker? marker;
        lock (_markerGate)
        {
            marker = _remoteMarker;
        }

        if (marker is null || marker.SequenceNumber != sequence)
        {
            return false;
        }

        if (marker.Hash.Length == 0)
        {
            return true;
        }

        ClipboardSnapshot? snapshot = null;
        try
        {
            snapshot = await ClipboardBridge.TryReadWithRetryAsync(sequence, CancellationToken.None);
        }
        catch
        {
        }

        if (snapshot is null)
        {
            // Keep the marker for another notification in the same clipboard
            // transaction. Failing open here could echo a remote image back.
            return true;
        }

        if (CryptographicOperations.FixedTimeEquals(snapshot.Hash, marker.Hash))
        {
            // Keep the sequence/hash marker until a different sequence arrives;
            // SetImage can generate more than one WM_CLIPBOARDUPDATE message.
            return true;
        }

        lock (_markerGate)
        {
            if (_remoteMarker?.SequenceNumber == sequence)
            {
                _remoteMarker = null;
            }
        }

        return false;
    }

    private void StartServices()
    {
        if (!_configured || _key is null || _config is null || _config.Paused || _servicesActive)
        {
            UpdateMenuState();
            return;
        }

        try
        {
            if (!NativeMethods.AddClipboardFormatListener(Handle))
            {
                throw new InvalidOperationException("Unable to register clipboard listener.");
            }

            LightClipListener listener = new(_key, _replayWindow, ApplyIncomingPacketAsync);
            _listener = listener;
            listener.Start();
            StreamTransfer.CleanupAbandonedTransfers(TransferCache);
            var streamListener = new TcpListener(IPAddress.IPv6Any, StreamProtocol.Port);
            streamListener.Server.DualMode = true;
            streamListener.Start(4);
            _streamListener = streamListener;
            _streamStop = new CancellationTokenSource();
            _servicesActive = true;
            _ = AcceptStreamsAsync(streamListener, _key.ToArray(), _streamStop);
            SetStatus("已启用");
        }
        catch
        {
            NativeMethods.RemoveClipboardFormatListener(Handle);
            _listener?.Dispose();
            _listener = null;
            _streamListener?.Stop(); _streamListener = null;
            _streamStop?.Cancel(); _streamStop = null;
            _servicesActive = false;
            SetStatus("监听未启动");
            MessageBox.Show("无法启动剪贴板监听，请确认端口 49287 和 49289 未被占用。", "LightClip", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        UpdateMenuState();
    }

    private void StopServices()
    {
        _servicesActive = false;
        CancelCurrentTransfers();
        _streamStop?.Cancel(); _streamStop = null;
        _streamListener?.Stop(); _streamListener = null;
        try
        {
            if (IsHandleCreated)
            {
                NativeMethods.RemoveClipboardFormatListener(Handle);
            }
        }
        catch
        {
        }

        LightClipListener? listener = _listener;
        _listener = null;
        listener?.Dispose();
        UpdateMenuState();
    }

    private void SetStatus(string status)
    {
        if (IsDisposed)
        {
            return;
        }

        _statusItem.Text = $"状态：{status}";
        _trayIcon.Text = status.Length > 55 ? $"LightClip：{status[..55]}" : $"LightClip：{status}";
    }

    private void UpdateMenuState()
    {
        _pauseItem.Enabled = _configured;
        _sendItem.Enabled = _servicesActive;
        _pauseItem.Text = _config?.Paused == true ? "恢复同步" : "暂停同步";
    }

    public void SuppressCurrentClipboardSequence()
    {
        lock (_markerGate)
        {
            _suppressedClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
        }
    }

    private sealed record ClipboardMarker(uint SequenceNumber, byte[] Hash);
}

internal sealed record SettingsDraft(string PeerAddress, byte[] SharedKey, bool RunAtLogin);

internal sealed class SettingsForm : Form
{
    private readonly MainForm _owner;
    private readonly TextBox _peerTextBox;
    private readonly TextBox _keyTextBox;
    private readonly CheckBox _runAtLoginCheckBox;
    private readonly Label _messageLabel;

    public SettingsForm(MainForm owner, string? peerAddress, string? encodedKey, bool runAtLogin)
    {
        _owner = owner;
        Text = "LightClip 设置";
        Icon = owner.CreateApplicationIcon();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 300);

        Label peerLabel = new()
        {
            Text = "对端 IP 地址（IPv4 或 IPv6，不含端口）：",
            AutoSize = true,
            Location = new Point(24, 24),
        };
        _peerTextBox = new TextBox
        {
            Location = new Point(24, 48),
            Width = 470,
            Text = peerAddress ?? string.Empty,
        };

        Label portLabel = new()
        {
            Text = $"端口：{LightClipProtocol.Port}（固定）",
            AutoSize = true,
            Location = new Point(24, 82),
        };
        Label ownAddressLabel = new()
        {
            Text = $"本机地址：{GetOwnAddresses()}",
            AutoSize = true,
            Location = new Point(24, 108),
            MaximumSize = new Size(470, 0),
        };

        Label keyLabel = new()
        {
            Text = "共享密钥（Base64，32 字节）：",
            AutoSize = true,
            Location = new Point(24, 144),
        };
        _keyTextBox = new TextBox
        {
            Location = new Point(24, 168),
            Width = 340,
            UseSystemPasswordChar = true,
            Text = encodedKey ?? string.Empty,
        };
        Button generateButton = new()
        {
            Text = "生成密钥",
            Location = new Point(372, 166),
            Width = 122,
        };
        generateButton.Click += (_, _) => GenerateKey();

        Button copyButton = new()
        {
            Text = "复制密钥",
            Location = new Point(372, 199),
            Width = 122,
        };
        copyButton.Click += (_, _) => CopyKey();

        _runAtLoginCheckBox = new CheckBox
        {
            Text = "登录 Windows 时自动启动（可选）",
            AutoSize = true,
            Location = new Point(24, 204),
            Checked = runAtLogin,
        };

        _messageLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.DarkSlateGray,
            Location = new Point(24, 238),
            Text = "首次保存并启用后才会监听剪贴板。",
        };

        Button saveButton = new()
        {
            Text = "保存并启用",
            Location = new Point(274, 264),
            Width = 105,
        };
        saveButton.Click += SaveButton_Click;
        Button cancelButton = new()
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(389, 264),
            Width = 105,
        };

        Controls.AddRange(new Control[]
        {
            peerLabel, _peerTextBox, portLabel, ownAddressLabel, keyLabel, _keyTextBox,
            generateButton, copyButton, _runAtLoginCheckBox, _messageLabel, saveButton, cancelButton,
        });
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    public SettingsDraft? Draft { get; private set; }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        if (!IPAddress.TryParse(_peerTextBox.Text.Trim(), out IPAddress? peer) ||
            peer.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            ShowValidationError("请输入有效的 IPv4 或 IPv6 地址。", keepOpen: true);
            return;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(_keyTextBox.Text.Trim());
        }
        catch (FormatException)
        {
            ShowValidationError("共享密钥必须是有效的 Base64。", keepOpen: true);
            return;
        }

        if (key.Length != LightClipProtocol.AesKeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            ShowValidationError("共享密钥解码后必须正好是 32 字节。", keepOpen: true);
            return;
        }

        Draft = new SettingsDraft(peer.ToString(), key, _runAtLoginCheckBox.Checked);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void GenerateKey()
    {
        byte[] key = RandomNumberGenerator.GetBytes(LightClipProtocol.AesKeyBytes);
        _keyTextBox.Text = Convert.ToBase64String(key);
        CryptographicOperations.ZeroMemory(key);
        _messageLabel.Text = "已生成新密钥；请在另一端手动填写同一密钥。";
    }

    private void CopyKey()
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(_keyTextBox.Text.Trim());
        }
        catch (FormatException)
        {
            ShowValidationError("请先填写有效的 Base64 密钥。", keepOpen: true);
            return;
        }

        if (key.Length != LightClipProtocol.AesKeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            ShowValidationError("共享密钥解码后必须正好是 32 字节。", keepOpen: true);
            return;
        }

        try
        {
            DataObject concealedKey = new();
            concealedKey.SetData(DataFormats.UnicodeText, _keyTextBox.Text.Trim());
            concealedKey.SetData("org.nspasteboard.ConcealedType", Array.Empty<byte>());
            Clipboard.SetDataObject(concealedKey, copy: true);
            _owner.SuppressCurrentClipboardSequence();
            _messageLabel.Text = "密钥已复制到剪贴板。";
        }
        catch
        {
            ShowValidationError("复制密钥失败。", keepOpen: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private void ShowValidationError(string message, bool keepOpen)
    {
        _messageLabel.Text = message;
        if (!keepOpen)
        {
            MessageBox.Show(this, message, "LightClip", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string GetOwnAddresses()
    {
        try
        {
            string[] addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => !IPAddress.IsLoopback(address))
                .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return addresses.Length == 0 ? "未检测到" : string.Join(", ", addresses);
        }
        catch
        {
            return "未检测到";
        }
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();
}
