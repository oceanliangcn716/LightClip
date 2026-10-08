using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LightClip;

internal sealed record Packet(Guid Id, ulong Timestamp, byte Kind, byte[] Body);

internal static class Protocol
{
    public const int Port = 49287, TextLimit = 1_048_576, ImageLimit = 8_388_608;
    public const int MinEnvelope = 57, MaxEnvelope = ImageLimit + 57;
    public static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly byte[] Aad = "LightClip/v1"u8.ToArray();
    public static ulong Now => checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    public static Packet New(byte kind, byte[]? body = null) => new(Guid.NewGuid(), Now, kind, body ?? []);

    public static byte[] Encode(Packet p, byte[] key)
    {
        ValidateBody(p.Kind, p.Body);
        if (key.Length != 32) throw new CryptographicException();
        byte[] plain = new byte[29 + p.Body.Length];
        "LCP1"u8.CopyTo(plain);
        p.Id.TryWriteBytes(plain.AsSpan(4, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(plain.AsSpan(20), p.Timestamp);
        plain[28] = p.Kind;
        p.Body.CopyTo(plain, 29);
        byte[] frame = new byte[4 + plain.Length + 28];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(frame.Length - 4));
        RandomNumberGenerator.Fill(frame.AsSpan(4, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(frame.AsSpan(4, 12), plain, frame.AsSpan(16, plain.Length), frame.AsSpan(16 + plain.Length, 16), Aad);
        CryptographicOperations.ZeroMemory(plain);
        return frame;
    }

    public static Packet Decode(byte[] envelope, byte[] key)
    {
        if (envelope.Length < MinEnvelope || envelope.Length > MaxEnvelope || key.Length != 32) throw new InvalidDataException();
        byte[] plain = new byte[envelope.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.AsSpan(0, 12), envelope.AsSpan(12, plain.Length), envelope.AsSpan(12 + plain.Length, 16), plain, Aad);
            if (!plain.AsSpan(0, 4).SequenceEqual("LCP1"u8)) throw new InvalidDataException();
            ulong ts = BinaryPrimitives.ReadUInt64BigEndian(plain.AsSpan(20));
            ulong now = Now;
            if ((ts > now ? ts - now : now - ts) > 120_000) throw new InvalidDataException();
            byte kind = plain[28];
            byte[] body = plain.AsSpan(29).ToArray();
            ValidateBody(kind, body);
            return new(new Guid(plain.AsSpan(4, 16), bigEndian: true), ts, kind, body);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void ValidateBody(byte kind, ReadOnlySpan<byte> body)
    {
        switch (kind)
        {
            case 0 or 3 when body.Length == 0: return;
            case 1:
                if (body.Length > TextLimit || body.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw new InvalidDataException();
                _ = Utf8.GetCharCount(body);
                return;
            case 2: Png.Validate(body); return;
            default: throw new InvalidDataException();
        }
    }

    public static async Task<Packet> ReadAsync(NetworkStream stream, byte[] key, CancellationToken ct)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (length < MinEnvelope || length > MaxEnvelope) throw new InvalidDataException();
        byte[] envelope = new byte[(int)length];
        await stream.ReadExactlyAsync(envelope, ct);
        return Decode(envelope, key);
    }

    public static void ValidateAck(Packet request, Packet ack)
    {
        if (ack.Kind != 3 || ack.Id != request.Id || ack.Body.Length != 0) throw new InvalidDataException();
    }
}

internal sealed class ReplayWindow
{
    readonly Dictionary<Guid, long> accepted = [];
    readonly object gate = new();
    public bool Accept(Guid id, long? time = null)
    {
        lock (gate)
        {
            long now = time ?? Environment.TickCount64;
            foreach (var old in accepted.Where(x => now - x.Value >= 300_000).Select(x => x.Key).ToArray()) accepted.Remove(old);
            if (accepted.ContainsKey(id) || accepted.Count >= 4096) return false;
            accepted.Add(id, now);
            return true;
        }
    }
}

internal static class Png
{
    static readonly uint[] CrcTable=MakeCrcTable();
    static uint[] MakeCrcTable(){var table=new uint[256];for(uint i=0;i<256;i++){uint crc=i;for(int j=0;j<8;j++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);table[i]=crc;}return table;}
    public static (int Width, int Height) Validate(ReadOnlySpan<byte> b)
    {
        if (b.Length > Protocol.ImageLimit || b.Length < 45 || !b[..8].SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })) throw new InvalidDataException();
        if (BinaryPrimitives.ReadUInt32BigEndian(b[8..]) != 13 || !b.Slice(12,4).SequenceEqual("IHDR"u8)) throw new InvalidDataException();
        uint w = BinaryPrimitives.ReadUInt32BigEndian(b[16..]), h = BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
        if (w == 0 || h == 0 || (ulong)w * h > 16_000_000) throw new InvalidDataException();
        int offset = 8; bool idat = false, end = false;
        while (offset <= b.Length - 12)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(b[offset..]);
            if (size > (uint)(b.Length - offset - 12)) throw new InvalidDataException();
            var typeData = b.Slice(offset + 4, (int)size + 4);
            uint crc = 0xffffffff;
            foreach (byte v in typeData) crc=(crc>>8)^CrcTable[(crc^v)&255];
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(b[(offset + 8 + (int)size)..])) throw new InvalidDataException();
            if (typeData[..4].SequenceEqual("IDAT"u8)) idat = true;
            if (typeData[..4].SequenceEqual("IEND"u8)) { if (size != 0) throw new InvalidDataException(); end = true; }
            offset += checked((int)size + 12);
            if (end) break;
        }
        if (!idat || !end || offset != b.Length) throw new InvalidDataException();
        return ((int)w, (int)h);
    }
    public static Bitmap Decode(byte[] b)
    {
        var (w,h) = Validate(b);
        using var ms = new MemoryStream(b, false);
        using var image = Image.FromStream(ms, false, true);
        if (image.Width != w || image.Height != h) throw new InvalidDataException();
        var result = new Bitmap(w,h,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try { using var g = Graphics.FromImage(result); g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImage(image,0,0,w,h); return result; }
        catch { result.Dispose(); throw; }
    }
}
