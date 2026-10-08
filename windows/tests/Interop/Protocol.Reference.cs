using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LightClip.Windows;

/// <summary>
/// The wire-level packet kinds used by LightClip/v1.
/// </summary>
public enum LightClipPacketKind : byte
{
    Ping = 0,
    Text = 1,
    Png = 2,
    Ack = 3,
}

/// <summary>
/// A decrypted LightClip packet. Body is owned by the packet and is copied on construction.
/// </summary>
public sealed class LightClipPacket
{
    public LightClipPacket(Guid requestId, ulong timestampMilliseconds, LightClipPacketKind kind, ReadOnlySpan<byte> body)
    {
        RequestId = requestId;
        TimestampMilliseconds = timestampMilliseconds;
        Kind = kind;
        Body = body.ToArray();
    }

    public Guid RequestId { get; }

    public ulong TimestampMilliseconds { get; }

    public LightClipPacketKind Kind { get; }

    public byte[] Body { get; }
}

/// <summary>
/// Exceptions raised for malformed or unauthenticated protocol data.
/// The exception text intentionally contains no packet content or key material.
/// </summary>
public sealed class LightClipProtocolException : Exception
{
    public LightClipProtocolException(string message)
        : base(message)
    {
    }

    public LightClipProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// LightClip/v1 framing and authenticated-encryption implementation.
/// </summary>
public static class LightClipProtocol
{
    public const int Port = 49287;
    public const int AesKeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int EnvelopeOverheadBytes = NonceBytes + TagBytes;
    public const int PlaintextHeaderBytes = 4 + 16 + 8 + 1;
    public const int MaxTextBytes = 1 * 1024 * 1024;
    public const int MaxPngBytes = 8 * 1024 * 1024;
    public const int MaxPlaintextBytes = MaxPngBytes + PlaintextHeaderBytes;
    public const int MaxEnvelopeBytes = MaxPlaintextBytes + EnvelopeOverheadBytes;
    public const int MaxReplayEntries = 4096;
    public const long ReplayAgeMilliseconds = 5 * 60 * 1000L;
    public const long TimestampToleranceMilliseconds = 120 * 1000L;
    public const ulong MaxPngPixels = 16_000_000UL;

    private static readonly byte[] Aad = Encoding.ASCII.GetBytes("LightClip/v1");
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LCP1");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] PngSignature =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    };

    public static LightClipPacket CreatePing(Guid requestId, long? timestampMilliseconds = null)
    {
        return new LightClipPacket(
            requestId,
            ToTimestamp(timestampMilliseconds),
            LightClipPacketKind.Ping,
            ReadOnlySpan<byte>.Empty);
    }

    public static LightClipPacket CreateText(string text, Guid? requestId = null, long? timestampMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] body;
        try
        {
            body = StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException ex)
        {
            throw new LightClipProtocolException("Text is not valid UTF-8.", ex);
        }

        if (body.Length > MaxTextBytes)
        {
            throw new LightClipProtocolException("Text body is too large.");
        }

        return new LightClipPacket(
            requestId ?? Guid.NewGuid(),
            ToTimestamp(timestampMilliseconds),
            LightClipPacketKind.Text,
            body);
    }

    public static LightClipPacket CreatePng(ReadOnlySpan<byte> png, Guid? requestId = null, long? timestampMilliseconds = null)
    {
        ValidatePng(png);
        return new LightClipPacket(
            requestId ?? Guid.NewGuid(),
            ToTimestamp(timestampMilliseconds),
            LightClipPacketKind.Png,
            png);
    }

    public static LightClipPacket CreateAck(Guid requestId, long? timestampMilliseconds = null)
    {
        return new LightClipPacket(
            requestId,
            ToTimestamp(timestampMilliseconds),
            LightClipPacketKind.Ack,
            ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Encodes the four-byte big-endian envelope length followed by the AES-256-GCM envelope.
    /// Envelope bytes are nonce, ciphertext, then tag; AAD is the ASCII LightClip/v1 string.
    /// </summary>
    public static byte[] EncodeFrame(LightClipPacket packet, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ValidateKey(key);
        ValidatePacket(packet, validateTimestamp: false);

        int plaintextLength = PlaintextHeaderBytes + packet.Body.Length;
        if (plaintextLength > MaxPlaintextBytes)
        {
            throw new LightClipProtocolException("Plaintext is too large.");
        }

        byte[] plaintext = new byte[plaintextLength];
        Magic.CopyTo(plaintext, 0);
        WriteGuidNetworkOrder(packet.RequestId, plaintext.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt64BigEndian(plaintext.AsSpan(20, 8), packet.TimestampMilliseconds);
        plaintext[28] = (byte)packet.Kind;
        packet.Body.AsSpan().CopyTo(plaintext.AsSpan(PlaintextHeaderBytes));

        int envelopeLength = checked(plaintextLength + EnvelopeOverheadBytes);
        if (envelopeLength > MaxEnvelopeBytes)
        {
            throw new LightClipProtocolException("Envelope is too large.");
        }

        byte[] frame = new byte[4 + envelopeLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), checked((uint)envelopeLength));
        Span<byte> nonce = frame.AsSpan(4, NonceBytes);
        Span<byte> ciphertext = frame.AsSpan(4 + NonceBytes, plaintextLength);
        Span<byte> tag = frame.AsSpan(4 + NonceBytes + plaintextLength, TagBytes);
        RandomNumberGenerator.Fill(nonce);

        using (AesGcm aes = new(key.ToArray(), TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad);
        }

        CryptographicOperations.ZeroMemory(plaintext);
        return frame;
    }

    /// <summary>
    /// Decrypts an envelope without its four-byte frame length prefix and validates all fields.
    /// </summary>
    public static LightClipPacket DecodeEnvelope(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, long? nowUnixMilliseconds = null)
    {
        ValidateKey(key);
        if (envelope.Length < EnvelopeOverheadBytes || envelope.Length > MaxEnvelopeBytes)
        {
            throw new LightClipProtocolException("Envelope length is invalid.");
        }

        int ciphertextLength = envelope.Length - EnvelopeOverheadBytes;
        if (ciphertextLength < PlaintextHeaderBytes || ciphertextLength > MaxPlaintextBytes)
        {
            throw new LightClipProtocolException("Plaintext length is invalid.");
        }

        byte[] plaintext = new byte[ciphertextLength];
        try
        {
            ReadOnlySpan<byte> nonce = envelope[..NonceBytes];
            ReadOnlySpan<byte> ciphertext = envelope.Slice(NonceBytes, ciphertextLength);
            ReadOnlySpan<byte> tag = envelope[^TagBytes..];
            using (AesGcm aes = new(key.ToArray(), TagBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad);
            }
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new LightClipProtocolException("Envelope authentication failed.", ex);
        }

        try
        {
            if (!plaintext.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                throw new LightClipProtocolException("Packet magic is invalid.");
            }

            Guid requestId = ReadGuidNetworkOrder(plaintext.AsSpan(4, 16));
            ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(plaintext.AsSpan(20, 8));
            ValidateTimestamp(timestamp, nowUnixMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            LightClipPacketKind kind = (LightClipPacketKind)plaintext[28];
            ReadOnlySpan<byte> body = plaintext.AsSpan(PlaintextHeaderBytes);
            ValidateKindAndBody(kind, body);
            return new LightClipPacket(requestId, timestamp, kind, body);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Validates a frame that includes its four-byte big-endian envelope length prefix.
    /// </summary>
    public static LightClipPacket DecodeFrame(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> key, long? nowUnixMilliseconds = null)
    {
        ValidateKey(key);
        if (frame.Length < 4)
        {
            throw new LightClipProtocolException("Frame is too short.");
        }

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(frame[..4]);
        if (declaredLength < EnvelopeOverheadBytes || declaredLength > MaxEnvelopeBytes)
        {
            throw new LightClipProtocolException("Declared envelope length is invalid.");
        }

        if (frame.Length != checked(4 + (int)declaredLength))
        {
            throw new LightClipProtocolException("Frame length does not match its prefix.");
        }

        return DecodeEnvelope(frame[4..], key, nowUnixMilliseconds);
    }

    public static void ValidatePng(ReadOnlySpan<byte> png)
    {
        if (png.Length > MaxPngBytes || png.Length < 33 || !png[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            throw new LightClipProtocolException("PNG data is invalid or too large.");
        }

        uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(8, 4));
        if (chunkLength != 13 || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new LightClipProtocolException("PNG header is invalid.");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (width == 0 || height == 0 || (ulong)width * height > MaxPngPixels)
        {
            throw new LightClipProtocolException("PNG dimensions are invalid.");
        }
    }

    public static bool IsRequestKind(LightClipPacketKind kind)
    {
        return kind is LightClipPacketKind.Ping or LightClipPacketKind.Text or LightClipPacketKind.Png;
    }

    public static string DecodeText(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxTextBytes)
        {
            throw new LightClipProtocolException("Text body is too large.");
        }

        try
        {
            return StrictUtf8.GetString(utf8);
        }
        catch (DecoderFallbackException ex)
        {
            throw new LightClipProtocolException("Text is not valid UTF-8.", ex);
        }
    }

    private static void ValidatePacket(LightClipPacket packet, bool validateTimestamp)
    {
        if (validateTimestamp)
        {
            ValidateTimestamp(packet.TimestampMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        ValidateKindAndBody(packet.Kind, packet.Body);
    }

    private static void ValidateKindAndBody(LightClipPacketKind kind, ReadOnlySpan<byte> body)
    {
        switch (kind)
        {
            case LightClipPacketKind.Ping:
            case LightClipPacketKind.Ack:
                if (!body.IsEmpty)
                {
                    throw new LightClipProtocolException("Control packet body is not empty.");
                }

                break;

            case LightClipPacketKind.Text:
                _ = DecodeText(body);
                break;

            case LightClipPacketKind.Png:
                ValidatePng(body);
                break;

            default:
                throw new LightClipProtocolException("Packet kind is invalid.");
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != AesKeyBytes)
        {
            throw new LightClipProtocolException("AES-256 key length is invalid.");
        }
    }

    private static ulong ToTimestamp(long? timestampMilliseconds)
    {
        long timestamp = timestampMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (timestamp < 0)
        {
            throw new LightClipProtocolException("Timestamp is invalid.");
        }

        return checked((ulong)timestamp);
    }

    private static void ValidateTimestamp(ulong timestamp, long nowUnixMilliseconds)
    {
        if (timestamp > long.MaxValue || nowUnixMilliseconds < 0)
        {
            throw new LightClipProtocolException("Timestamp is invalid.");
        }

        long value = (long)timestamp;
        long difference = value >= nowUnixMilliseconds
            ? value - nowUnixMilliseconds
            : nowUnixMilliseconds - value;
        if (difference > TimestampToleranceMilliseconds)
        {
            throw new LightClipProtocolException("Timestamp is outside the allowed window.");
        }
    }

    // RFC 4122/network byte order. Guid's legacy byte-array representation reverses
    // the first three fields, so conversion is kept explicit for cross-platform parity.
    private static void WriteGuidNetworkOrder(Guid value, Span<byte> destination)
    {
        Span<byte> legacy = stackalloc byte[16];
        value.TryWriteBytes(legacy);
        destination[0] = legacy[3];
        destination[1] = legacy[2];
        destination[2] = legacy[1];
        destination[3] = legacy[0];
        destination[4] = legacy[5];
        destination[5] = legacy[4];
        destination[6] = legacy[7];
        destination[7] = legacy[6];
        legacy[8..].CopyTo(destination[8..]);
    }

    private static Guid ReadGuidNetworkOrder(ReadOnlySpan<byte> source)
    {
        Span<byte> legacy = stackalloc byte[16];
        legacy[0] = source[3];
        legacy[1] = source[2];
        legacy[2] = source[1];
        legacy[3] = source[0];
        legacy[4] = source[5];
        legacy[5] = source[4];
        legacy[6] = source[7];
        legacy[7] = source[6];
        source[8..].CopyTo(legacy[8..]);
        return new Guid(legacy);
    }
}

/// <summary>
/// A bounded replay UUID window. It stores only UUIDs and timestamps, never packet data.
/// </summary>
public sealed class LightClipReplayWindow
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, long> _entries = new();
    private readonly Queue<(Guid Id, long Timestamp)> _order = new();

    public bool TryAdd(Guid requestId, long nowUnixMilliseconds)
    {
        lock (_gate)
        {
            Prune(nowUnixMilliseconds);
            if (_entries.ContainsKey(requestId))
            {
                return false;
            }

            if (_entries.Count >= LightClipProtocol.MaxReplayEntries)
            {
                return false;
            }

            _entries[requestId] = nowUnixMilliseconds;
            _order.Enqueue((requestId, nowUnixMilliseconds));
            return true;
        }
    }

    private void Prune(long nowUnixMilliseconds)
    {
        while (_order.Count > 0)
        {
            (Guid id, long timestamp) = _order.Peek();
            long age = nowUnixMilliseconds >= timestamp ? nowUnixMilliseconds - timestamp : 0;
            if (age <= LightClipProtocol.ReplayAgeMilliseconds)
            {
                break;
            }

            _order.Dequeue();
            if (_entries.TryGetValue(id, out long current) && current == timestamp)
            {
                _entries.Remove(id);
            }
        }
    }
}
