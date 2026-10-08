using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightClip.Windows;

internal static class Program
{
    private const int PairPort = 49388;
    private const int MaximumJsonBytes = 4096;
    private const string TestCode = "12345678";
    private const string TestName = "Windows 合成测试";

    private static readonly Guid ClientID = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid HostID = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid GroupID = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly byte[] TestGroupKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            ConfigureNativeResolver();
            string mode = args.Length == 0 ? "native" : args[0].ToLowerInvariant();
            switch (mode)
            {
                case "native":
                    RunNativeChecks();
                    Console.WriteLine("PASS: native");
                    return 0;

                case "server":
                    await RunServerAsync();
                    Console.WriteLine("PASS: server");
                    return 0;

                case "client":
                    await RunClientAsync();
                    Console.WriteLine("PASS: client");
                    return 0;

                default:
                    throw new ArgumentException();
            }
        }
        catch (Exception error)
        {
            // Keep the test result useful without exposing code, keys, paths,
            // peer messages, or serialized JSON.
            Console.WriteLine($"FAIL: {error.GetType().Name}");
            return 1;
        }
    }

    private static void ConfigureNativeResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(PairingSpake).Assembly, static (name, _, _) =>
        {
            if (!string.Equals(name, "lightclip_pairing", StringComparison.Ordinal))
            {
                return IntPtr.Zero;
            }

            string[] candidates =
            {
                Path.Combine(Directory.GetCurrentDirectory(), "pairing", "target", "release", "liblightclip_pairing.dylib"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../pairing/target/release/liblightclip_pairing.dylib")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../pairing/target/release/liblightclip_pairing.dylib")),
            };

            foreach (string candidate in candidates.Distinct(StringComparer.Ordinal))
            {
                if (File.Exists(candidate))
                {
                    return NativeLibrary.Load(candidate);
                }
            }

            return IntPtr.Zero;
        });
    }

    private static void RunNativeChecks()
    {
        byte[] sharedA = Array.Empty<byte>();
        byte[] sharedB = Array.Empty<byte>();
        try
        {
            using var a = new PairingSpake(0, TestCode, ClientID, HostID, GroupID);
            using var b = new PairingSpake(1, TestCode, ClientID, HostID, GroupID);
            byte[] aMessage = a.Message;
            byte[] bMessage = b.Message;
            sharedA = a.Finish(bMessage);
            sharedB = b.Finish(aMessage);
            Require(CryptographicOperations.FixedTimeEquals(sharedA, sharedB));
        }
        finally
        {
            Zero(sharedA);
            Zero(sharedB);
        }

        byte[] wrongAKey = Array.Empty<byte>();
        byte[] wrongBKey = Array.Empty<byte>();
        try
        {
            using var wrongA = new PairingSpake(0, "12345679", ClientID, HostID, GroupID);
            using var rightB = new PairingSpake(1, TestCode, ClientID, HostID, GroupID);
            byte[] wrongAMessage = wrongA.Message;
            byte[] rightBMessage = rightB.Message;
            wrongAKey = wrongA.Finish(rightBMessage);
            wrongBKey = rightB.Finish(wrongAMessage);
            Require(!CryptographicOperations.FixedTimeEquals(wrongAKey, wrongBKey));
        }
        finally
        {
            Zero(wrongAKey);
            Zero(wrongBKey);
        }

        byte[] changedAKey = Array.Empty<byte>();
        byte[] changedBKey = Array.Empty<byte>();
        try
        {
            Guid changedHost = Guid.Parse("66666666-7777-8888-9999-000000000000");
            using var originalA = new PairingSpake(0, TestCode, ClientID, HostID, GroupID);
            using var changedB = new PairingSpake(1, TestCode, ClientID, changedHost, GroupID);
            byte[] originalAMessage = originalA.Message;
            byte[] changedBMessage = changedB.Message;
            changedAKey = originalA.Finish(changedBMessage);
            changedBKey = changedB.Finish(originalAMessage);
            Require(!CryptographicOperations.FixedTimeEquals(changedAKey, changedBKey));
        }
        finally
        {
            Zero(changedAKey);
            Zero(changedBKey);
        }

        using (var repeatA = new PairingSpake(0, TestCode, ClientID, HostID, GroupID))
        using (var repeatB = new PairingSpake(1, TestCode, ClientID, HostID, GroupID))
        {
            byte[] repeatMessage = repeatB.Message;
            byte[] repeatKey = repeatA.Finish(repeatMessage);
            Zero(repeatKey);
            RequireRejected(() => repeatA.Finish(repeatMessage));
        }

        using (var malformed = new PairingSpake(0, TestCode, ClientID, HostID, GroupID))
        {
            RequireRejected(() => malformed.Finish(new byte[PairingSpake.MessageBytes]));
        }
    }

    private static async Task RunServerAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, PairPort);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        using var acceptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using TcpClient client = await listener.AcceptTcpClientAsync(acceptDeadline.Token);
        using var deadline = new Deadline(TimeSpan.FromSeconds(5));
        client.NoDelay = true;
        await using NetworkStream stream = client.GetStream();

        PairFrame hello = await ReadFrameAsync(stream, deadline);
        Require(hello.Version == 2 && hello.Type == "pair-hello");
        Require(hello.DeviceId == Canonical(ClientID));
        Require(!string.IsNullOrWhiteSpace(hello.Name) && System.Text.Encoding.UTF8.GetByteCount(hello.Name) <= 96 && !hello.Name.Any(char.IsControl));
        Require(hello.GroupId == Canonical(GroupID));
        byte[] messageA = DecodeBase64(hello.Spake, PairingSpake.MessageBytes);

        using var context = new PairingSpake(1, TestCode, ClientID, HostID, GroupID);
        byte[] messageB = context.Message;
        byte[] secret = context.Finish(messageA);
        byte[] salt = new byte[PairingCrypto.SaltBytes];
        RandomNumberGenerator.Fill(salt);
        byte[] transcript = PairingCrypto.Transcript(ClientID, HostID, GroupID, messageA, messageB, salt);
        byte[] pairKey = PairingCrypto.DeriveKey(secret, salt, transcript);
        byte[] serverProof = PairingCrypto.Proof(pairKey, "server-confirm", transcript);
        try
        {
            await WriteFrameAsync(stream, new PairFrame
            {
                Version = 2,
                Type = "pair-challenge",
                DeviceId = Canonical(HostID),
                GroupId = Canonical(GroupID),
                Spake = Convert.ToBase64String(messageB),
                Salt = Convert.ToBase64String(salt),
                Proof = Convert.ToBase64String(serverProof),
            }, deadline, fragmented: true);

            PairFrame proofMessage = await ReadFrameAsync(stream, deadline);
            Require(proofMessage.Version == 2 && proofMessage.Type == "pair-proof");
            byte[] clientProof = DecodeBase64(proofMessage.Proof, PairingCrypto.KeyBytes);
            try
            {
                Require(PairingCrypto.VerifyProof(clientProof, pairKey, "client-confirm", transcript));
            }
            finally
            {
                Zero(clientProof);
            }

            byte[] sealedGroup = PairingCrypto.WrapGroup(GroupID, TestGroupKey, pairKey, transcript);
            await WriteFrameAsync(stream, new PairFrame
            {
                Version = 2,
                Type = "pair-result",
                Sealed = Convert.ToBase64String(sealedGroup),
            }, deadline, fragmented: true);
            Zero(sealedGroup);

            PairFrame done = await ReadFrameAsync(stream, deadline);
            Require(done.Version == 2 && done.Type == "pair-done");
            byte[] storedProof = DecodeBase64(done.Proof, PairingCrypto.KeyBytes);
            try
            {
                Require(PairingCrypto.VerifyProof(storedProof, pairKey, "stored", transcript));
            }
            finally
            {
                Zero(storedProof);
            }
        }
        finally
        {
            Zero(secret);
            Zero(salt);
            Zero(transcript);
            Zero(pairKey);
            Zero(serverProof);
        }
    }

    private static async Task RunClientAsync()
    {
        using var deadline = new Deadline(TimeSpan.FromSeconds(5));
        using var context = new PairingSpake(0, TestCode, ClientID, HostID, GroupID);
        byte[] messageA = context.Message;
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, PairPort, deadline.Token);
        client.NoDelay = true;
        await using NetworkStream stream = client.GetStream();

        await WriteFrameAsync(stream, new PairFrame
        {
            Version = 2,
            Type = "pair-hello",
            DeviceId = Canonical(ClientID),
            Name = TestName,
            GroupId = Canonical(GroupID),
            Spake = Convert.ToBase64String(messageA),
        }, deadline, fragmented: true);

        PairFrame challenge = await ReadFrameAsync(stream, deadline);
        Require(challenge.Version == 2 && challenge.Type == "pair-challenge");
        Require(challenge.DeviceId == Canonical(HostID));
        Require(challenge.GroupId == Canonical(GroupID));
        byte[] messageB = DecodeBase64(challenge.Spake, PairingSpake.MessageBytes);
        byte[] salt = DecodeBase64(challenge.Salt, PairingCrypto.SaltBytes);
        byte[] serverProof = DecodeBase64(challenge.Proof, PairingCrypto.KeyBytes);
        byte[] secret = context.Finish(messageB);
        byte[] transcript = PairingCrypto.Transcript(ClientID, HostID, GroupID, messageA, messageB, salt);
        byte[] pairKey = PairingCrypto.DeriveKey(secret, salt, transcript);
        try
        {
            Require(PairingCrypto.VerifyProof(serverProof, pairKey, "server-confirm", transcript));
            byte[] clientProof = PairingCrypto.Proof(pairKey, "client-confirm", transcript);
            try
            {
                await WriteFrameAsync(stream, new PairFrame
                {
                    Version = 2,
                    Type = "pair-proof",
                    Proof = Convert.ToBase64String(clientProof),
                }, deadline, fragmented: true);
            }
            finally
            {
                Zero(clientProof);
            }

            PairFrame result = await ReadFrameAsync(stream, deadline);
            Require(result.Version == 2 && result.Type == "pair-result");
            byte[] sealedGroup = DecodeBase64(result.Sealed, PairingCrypto.WrappedGroupBytes);
            byte[] unwrapped = PairingCrypto.UnwrapGroup(GroupID, sealedGroup, pairKey, transcript);
            try
            {
                Require(CryptographicOperations.FixedTimeEquals(unwrapped, TestGroupKey));
            }
            finally
            {
                Zero(sealedGroup);
                Zero(unwrapped);
            }

            byte[] storedProof = PairingCrypto.Proof(pairKey, "stored", transcript);
            try
            {
                await WriteFrameAsync(stream, new PairFrame
                {
                    Version = 2,
                    Type = "pair-done",
                    Proof = Convert.ToBase64String(storedProof),
                }, deadline, fragmented: true);
            }
            finally
            {
                Zero(storedProof);
            }
        }
        finally
        {
            Zero(salt);
            Zero(serverProof);
            Zero(secret);
            Zero(transcript);
            Zero(pairKey);
        }
    }

    private static async Task WriteFrameAsync(
        NetworkStream stream,
        PairFrame message,
        Deadline deadline,
        bool fragmented)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length is < 1 or > MaximumJsonBytes)
        {
            throw new InvalidDataException();
        }

        byte[] frame = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, sizeof(uint)), checked((uint)payload.Length));
        payload.CopyTo(frame, sizeof(uint));
        if (!fragmented)
        {
            await stream.WriteAsync(frame, deadline.Token);
            return;
        }

        for (int offset = 0; offset < frame.Length; offset += 3)
        {
            deadline.ThrowIfExpired();
            int count = Math.Min(3, frame.Length - offset);
            await stream.WriteAsync(frame.AsMemory(offset, count), deadline.Token);
        }
    }

    private static async Task<PairFrame> ReadFrameAsync(NetworkStream stream, Deadline deadline)
    {
        byte[] header = new byte[sizeof(uint)];
        await ReadExactlyAsync(stream, header, deadline.Token);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is < 1 or > MaximumJsonBytes)
        {
            throw new InvalidDataException();
        }

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, deadline.Token);
        PairFrame? message = JsonSerializer.Deserialize<PairFrame>(payload, JsonOptions);
        if (message is null || message.Version != 2)
        {
            throw new JsonException();
        }

        return message;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> destination, CancellationToken token)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int count = await stream.ReadAsync(destination[offset..], token);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            offset += count;
        }
    }

    private static byte[] DecodeBase64(string? text, int expectedLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidDataException();
        }

        byte[] value;
        try
        {
            value = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new InvalidDataException();
        }

        if (value.Length != expectedLength || !string.Equals(Convert.ToBase64String(value), text, StringComparison.Ordinal))
        {
            Zero(value);
            throw new InvalidDataException();
        }

        return value;
    }

    private static string Canonical(Guid value)
    {
        return value.ToString("D").ToLowerInvariant();
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException();
        }
    }

    private static void RequireRejected(Action action)
    {
        try
        {
            action();
        }
        catch (PairingCryptoException)
        {
            return;
        }

        throw new InvalidDataException();
    }

    private static void Zero(byte[] value)
    {
        if (value.Length > 0)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private sealed class Deadline : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));

        public Deadline(TimeSpan duration)
        {
            cancellation.CancelAfter(duration);
        }

        public CancellationToken Token => cancellation.Token;

        public void ThrowIfExpired()
        {
            cancellation.Token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            cancellation.Dispose();
        }
    }

    private sealed class PairFrame
    {
        [JsonPropertyName("v")]
        public int Version { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("deviceId")]
        public string? DeviceId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("groupId")]
        public string? GroupId { get; set; }

        [JsonPropertyName("spake")]
        public string? Spake { get; set; }

        [JsonPropertyName("salt")]
        public string? Salt { get; set; }

        [JsonPropertyName("proof")]
        public string? Proof { get; set; }

        [JsonPropertyName("sealed")]
        public string? Sealed { get; set; }
    }
}
