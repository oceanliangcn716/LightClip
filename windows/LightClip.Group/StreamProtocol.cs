using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LightClip.Windows;

/// <summary>The authenticated stream record types from STREAM_V2.md.</summary>
public static class StreamRecordKind
{
    public const byte Offer = 10;
    public const byte Ready = 11;
    public const byte Chunk = 12;
    public const byte ChunkAck = 13;
    public const byte Finish = 14;
    public const byte Done = 15;
}

/// <summary>A file name and its exact byte length in a stream manifest.</summary>
public sealed record StreamItem(string Name, ulong Size);

/// <summary>The metadata authenticated by an offer record.</summary>
public sealed record StreamManifest(string Type, IReadOnlyList<StreamItem> Items, ulong Total);

/// <summary>A decrypted stream record. The body is copied on construction.</summary>
public sealed class StreamRecord
{
    public StreamRecord(Guid id, uint sequence, byte kind, ReadOnlySpan<byte> body)
    {
        Id = id;
        Sequence = sequence;
        Kind = kind;
        Body = body.ToArray();
    }

    public Guid Id { get; }

    public uint Sequence { get; }

    public byte Kind { get; }

    public byte[] Body { get; }
}

/// <summary>The paths committed by a successfully received stream.</summary>
public sealed record ReceivedStream(StreamManifest Manifest, IReadOnlyList<string> Paths);

/// <summary>Raised for malformed, unauthenticated, or invalid stream protocol data.</summary>
public sealed class StreamProtocolException : Exception
{
    public StreamProtocolException(string message)
        : base(message)
    {
    }

    public StreamProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// STREAM_V2 framing, AES-256-GCM, and manifest validation. This type has no
/// clipboard or filesystem side effects.
/// </summary>
public static class StreamProtocol
{
    public const int Port = 49289;
    public const int AesKeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int EnvelopeOverheadBytes = NonceBytes + TagBytes;
    public const int PlaintextHeaderBytes = 4 + 16 + 8 + 4 + 1;
    public const int ChunkMetadataBytes = 4 + 8;
    public const int MaxManifestBytes = 16_384;
    public const int MaxTextBytes = 10 * 1024 * 1024;
    public const ulong MaxFileBytes = 5_368_709_120UL;
    public const int MaxFileItems = 32;
    public const int ChunkBytes = 262_144;
    public const int MinEnvelopeBytes = PlaintextHeaderBytes + EnvelopeOverheadBytes;
    public const int MaxPlaintextBytes = PlaintextHeaderBytes + ChunkMetadataBytes + ChunkBytes;
    public const int MaxEnvelopeBytes = MaxPlaintextBytes + EnvelopeOverheadBytes;
    public const long TimestampToleranceMilliseconds = 120 * 1000L;

    private static readonly byte[] Aad = Encoding.ASCII.GetBytes("LightClip/stream/v2");
    private static readonly byte[] Magic = "LCS2"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Encodes a record as uint32-be(length), nonce, ciphertext, tag.</summary>
    public static byte[] EncodeFrame(StreamRecord record, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateKey(key);
        ValidateRecordBody(record.Kind, record.Body);

        int plaintextLength = checked(PlaintextHeaderBytes + record.Body.Length);
        if (plaintextLength > MaxPlaintextBytes)
        {
            throw new StreamProtocolException("Plaintext is too large.");
        }

        int envelopeLength = checked(plaintextLength + EnvelopeOverheadBytes);
        if (envelopeLength < MinEnvelopeBytes || envelopeLength > MaxEnvelopeBytes)
        {
            throw new StreamProtocolException("Envelope is too large.");
        }

        byte[] frame = new byte[4 + envelopeLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), checked((uint)envelopeLength));

        Span<byte> envelope = frame.AsSpan(4);
        Span<byte> nonce = envelope[..NonceBytes];
        Span<byte> ciphertext = envelope.Slice(NonceBytes, plaintextLength);
        Span<byte> tag = envelope.Slice(NonceBytes + plaintextLength, TagBytes);
        RandomNumberGenerator.Fill(nonce);

        byte[] plaintext = new byte[plaintextLength];
        try
        {
            Magic.CopyTo(plaintext, 0);
            WriteGuidNetworkOrder(record.Id, plaintext.AsSpan(4, 16));
            BinaryPrimitives.WriteUInt64BigEndian(plaintext.AsSpan(20, 8), CurrentTimestamp());
            BinaryPrimitives.WriteUInt32BigEndian(plaintext.AsSpan(28, 4), record.Sequence);
            plaintext[32] = record.Kind;
            record.Body.AsSpan().CopyTo(plaintext.AsSpan(PlaintextHeaderBytes));

            using AesGcm aes = new(key.ToArray(), TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return frame;
    }

    /// <summary>Decodes and authenticates a frame including its four-byte length.</summary>
    public static StreamRecord DecodeFrame(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> key, long? nowUnixMilliseconds = null)
    {
        ValidateKey(key);
        if (frame.Length < 4)
        {
            throw new StreamProtocolException("Frame is too short.");
        }

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(frame[..4]);
        if (declaredLength < MinEnvelopeBytes || declaredLength > MaxEnvelopeBytes)
        {
            throw new StreamProtocolException("Declared envelope length is invalid.");
        }

        if (frame.Length != checked(4 + (int)declaredLength))
        {
            throw new StreamProtocolException("Frame length does not match its prefix.");
        }

        return DecodeEnvelope(frame[4..], key, nowUnixMilliseconds);
    }

    /// <summary>
    /// Decodes and authenticates an envelope without its four-byte length prefix.
    /// Length checks happen before allocating ciphertext or plaintext buffers.
    /// </summary>
    public static StreamRecord DecodeEnvelope(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, long? nowUnixMilliseconds = null)
    {
        ValidateKey(key);
        if (envelope.Length < MinEnvelopeBytes || envelope.Length > MaxEnvelopeBytes)
        {
            throw new StreamProtocolException("Envelope length is invalid.");
        }

        int plaintextLength = envelope.Length - EnvelopeOverheadBytes;
        if (plaintextLength < PlaintextHeaderBytes || plaintextLength > MaxPlaintextBytes)
        {
            throw new StreamProtocolException("Plaintext length is invalid.");
        }

        byte[] plaintext = new byte[plaintextLength];
        try
        {
            ReadOnlySpan<byte> nonce = envelope[..NonceBytes];
            ReadOnlySpan<byte> ciphertext = envelope.Slice(NonceBytes, plaintextLength);
            ReadOnlySpan<byte> tag = envelope[^TagBytes..];
            try
            {
                using AesGcm aes = new(key.ToArray(), TagBytes);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad);
            }
            catch (CryptographicException ex)
            {
                throw new StreamProtocolException("Envelope authentication failed.", ex);
            }

            if (!plaintext.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                throw new StreamProtocolException("Record magic is invalid.");
            }

            Guid id = ReadGuidNetworkOrder(plaintext.AsSpan(4, 16));
            ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(plaintext.AsSpan(20, 8));
            ValidateTimestamp(timestamp, nowUnixMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            uint sequence = BinaryPrimitives.ReadUInt32BigEndian(plaintext.AsSpan(28, 4));
            byte kind = plaintext[32];
            ReadOnlySpan<byte> body = plaintext.AsSpan(PlaintextHeaderBytes);
            ValidateRecordBody(kind, body);
            return new StreamRecord(id, sequence, kind, body);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Serializes the exact offer JSON shape specified by STREAM_V2.md.</summary>
    public static byte[] EncodeManifest(StreamManifest manifest)
    {
        ValidateManifest(manifest);
        var writerBuffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(writerBuffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 2);
            writer.WriteString("type", manifest.Type);
            writer.WriteStartArray("items");
            foreach (StreamItem item in manifest.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("name", item.Name);
                writer.WriteNumber("size", item.Size);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("total", manifest.Total);
            writer.WriteEndObject();
        }

        if (writerBuffer.WrittenCount > MaxManifestBytes)
        {
            throw new StreamProtocolException("Manifest is too large.");
        }

        return writerBuffer.WrittenSpan.ToArray();
    }

    /// <summary>Parses and validates an offer manifest, including checked ulong sums.</summary>
    public static StreamManifest DecodeManifest(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0 || json.Length > MaxManifestBytes)
        {
            throw new StreamProtocolException("Manifest length is invalid.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new StreamProtocolException("Manifest root is invalid.");
            }

            int version = 0;
            string? type = null;
            JsonElement itemsElement = default;
            ulong total = 0;
            bool hasVersion = false, hasType = false, hasItems = false, hasTotal = false;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "v" when !hasVersion:
                        if (!property.Value.TryGetInt32(out version))
                        {
                            throw new StreamProtocolException("Manifest version is invalid.");
                        }

                        hasVersion = true;
                        break;
                    case "type" when !hasType:
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            throw new StreamProtocolException("Manifest type is invalid.");
                        }

                        type = property.Value.GetString();
                        hasType = true;
                        break;
                    case "items" when !hasItems:
                        if (property.Value.ValueKind != JsonValueKind.Array)
                        {
                            throw new StreamProtocolException("Manifest items are invalid.");
                        }

                        itemsElement = property.Value;
                        hasItems = true;
                        break;
                    case "total" when !hasTotal:
                        if (!property.Value.TryGetUInt64(out total))
                        {
                            throw new StreamProtocolException("Manifest total is invalid.");
                        }

                        hasTotal = true;
                        break;
                    default:
                        throw new StreamProtocolException("Manifest fields are invalid.");
                }
            }

            if (!hasVersion || !hasType || !hasItems || !hasTotal || version != 2 || type is null)
            {
                throw new StreamProtocolException("Manifest fields are incomplete.");
            }

            var items = new List<StreamItem>();
            foreach (JsonElement itemElement in itemsElement.EnumerateArray())
            {
                if (itemElement.ValueKind != JsonValueKind.Object)
                {
                    throw new StreamProtocolException("Manifest item is invalid.");
                }

                string? name = null;
                ulong size = 0;
                bool hasName = false, hasSize = false;
                foreach (JsonProperty property in itemElement.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "name" when !hasName:
                            if (property.Value.ValueKind != JsonValueKind.String)
                            {
                                throw new StreamProtocolException("Manifest item name is invalid.");
                            }

                            name = property.Value.GetString();
                            hasName = true;
                            break;
                        case "size" when !hasSize:
                            if (!property.Value.TryGetUInt64(out size))
                            {
                                throw new StreamProtocolException("Manifest item size is invalid.");
                            }

                            hasSize = true;
                            break;
                        default:
                            throw new StreamProtocolException("Manifest item fields are invalid.");
                    }
                }

                if (!hasName || !hasSize || name is null)
                {
                    throw new StreamProtocolException("Manifest item fields are incomplete.");
                }

                items.Add(new StreamItem(name, size));
            }

            var manifest = new StreamManifest(type, items, total);
            ValidateManifest(manifest);
            return manifest;
        }
        catch (StreamProtocolException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new StreamProtocolException("Manifest JSON is invalid.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new StreamProtocolException("Manifest JSON is invalid.", ex);
        }
    }

    /// <summary>Validates a manifest without serializing or touching the filesystem.</summary>
    public static void ValidateManifest(StreamManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Type is not ("files" or "text"))
        {
            throw new StreamProtocolException("Manifest type is invalid.");
        }

        if (manifest.Items is null || manifest.Items.Count == 0 || manifest.Items.Count > MaxFileItems)
        {
            throw new StreamProtocolException("Manifest item count is invalid.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ulong sum = 0;
        foreach (StreamItem? item in manifest.Items)
        {
            if (item is null)
            {
                throw new StreamProtocolException("Manifest item is invalid.");
            }

            ValidateItemName(item.Name);
            if (!names.Add(item.Name))
            {
                throw new StreamProtocolException("Manifest item names are duplicated.");
            }

            if (item.Size > MaxFileBytes)
            {
                throw new StreamProtocolException("Manifest item is too large.");
            }

            try
            {
                sum = checked(sum + item.Size);
            }
            catch (OverflowException ex)
            {
                throw new StreamProtocolException("Manifest total overflows.", ex);
            }
        }

        if (sum != manifest.Total || manifest.Total > MaxFileBytes)
        {
            throw new StreamProtocolException("Manifest total is invalid.");
        }

        if (manifest.Type == "text")
        {
            if (manifest.Items.Count != 1 || manifest.Items[0].Name != "clipboard.txt" || manifest.Total > (ulong)MaxTextBytes || manifest.Items[0].Size != manifest.Total)
            {
                throw new StreamProtocolException("Text manifest is invalid.");
            }
        }
    }

    /// <summary>Validates a Unix-millisecond timestamp against the protocol window.</summary>
    public static void ValidateTimestamp(ulong timestamp, long nowUnixMilliseconds)
    {
        if (timestamp > long.MaxValue || nowUnixMilliseconds < 0)
        {
            throw new StreamProtocolException("Timestamp is invalid.");
        }

        long value = (long)timestamp;
        long difference = value >= nowUnixMilliseconds ? value - nowUnixMilliseconds : nowUnixMilliseconds - value;
        if (difference > TimestampToleranceMilliseconds)
        {
            throw new StreamProtocolException("Timestamp is outside the allowed window.");
        }
    }

    public static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != AesKeyBytes)
        {
            throw new StreamProtocolException("AES-256 key length is invalid.");
        }
    }

    private static void ValidateRecordBody(byte kind, ReadOnlySpan<byte> body)
    {
        switch (kind)
        {
            case StreamRecordKind.Offer:
                if (body.Length == 0 || body.Length > MaxManifestBytes)
                {
                    throw new StreamProtocolException("Offer body is invalid.");
                }

                _ = DecodeManifest(body);
                break;
            case StreamRecordKind.Ready:
            case StreamRecordKind.ChunkAck:
            case StreamRecordKind.Done:
                if (!body.IsEmpty)
                {
                    throw new StreamProtocolException("Control body is invalid.");
                }

                break;
            case StreamRecordKind.Chunk:
                if (body.Length < ChunkMetadataBytes + 1 || body.Length > ChunkMetadataBytes + ChunkBytes)
                {
                    throw new StreamProtocolException("Chunk body is invalid.");
                }

                break;
            case StreamRecordKind.Finish:
                if (body.Length == 0 || body.Length > MaxFileItems * SHA256.HashSizeInBytes || body.Length % SHA256.HashSizeInBytes != 0)
                {
                    throw new StreamProtocolException("Finish body is invalid.");
                }

                break;
            default:
                throw new StreamProtocolException("Record kind is invalid.");
        }
    }

    private static void ValidateItemName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Length == 0)
        {
            throw new StreamProtocolException("Manifest item name is invalid.");
        }

        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(name);
        }
        catch (EncoderFallbackException ex)
        {
            throw new StreamProtocolException("Manifest item name is invalid.", ex);
        }

        if (utf8.Length > 255 || name[^1] == '.')
        {
            throw new StreamProtocolException("Manifest item name is invalid.");
        }

        foreach (char character in name)
        {
            if (char.IsControl(character) || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                throw new StreamProtocolException("Manifest item name is invalid.");
            }
        }

        if (name[^1] == ' ')
        {
            throw new StreamProtocolException("Manifest item name is invalid.");
        }

        int firstDot = name.IndexOf('.');
        string baseName = (firstDot < 0 ? name : name[..firstDot]).ToUpperInvariant();
        if (baseName is "CON" or "PRN" or "AUX" or "NUL" || IsNumberedWindowsDevice(baseName, "COM") || IsNumberedWindowsDevice(baseName, "LPT"))
        {
            throw new StreamProtocolException("Manifest item name is reserved.");
        }
    }

    private static bool IsNumberedWindowsDevice(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + 1)
        {
            return false;
        }

        return value[^1] is >= '1' and <= '9';
    }

    private static ulong CurrentTimestamp()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now < 0)
        {
            throw new StreamProtocolException("Timestamp is invalid.");
        }

        return checked((ulong)now);
    }

    // RFC 4122/network byte order. Guid.ToByteArray() uses the legacy little-endian
    // layout for the first three fields, so the conversion is intentionally explicit.
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
