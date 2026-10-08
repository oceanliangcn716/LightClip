using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LightClip.Windows;

/// <summary>
/// Pairing and wrapped group-key failures. Messages intentionally contain no
/// pairing code, key bytes, identity values, or other caller parameters.
/// </summary>
public sealed class PairingCryptoException : Exception
{
    public PairingCryptoException(string message)
        : base(message)
    {
    }
}

internal static class PairingNative
{
    [DllImport("lightclip_pairing", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr lc_spake_start(
        byte role,
        byte[]? password,
        nuint passwordLength,
        byte[]? idA,
        nuint idALength,
        byte[]? idB,
        nuint idBLength,
        byte[]? messageOut);

    [DllImport("lightclip_pairing", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int lc_spake_finish(
        IntPtr state,
        byte[]? peerMessage,
        nuint peerLength,
        byte[]? keyOut);

    [DllImport("lightclip_pairing", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void lc_spake_destroy(IntPtr state);
}

internal sealed class PairingStateHandle : SafeHandle
{
    private PairingStateHandle(IntPtr value)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(value);
    }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    public static PairingStateHandle Create(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            throw new PairingCryptoException("Pairing state is invalid.");
        }

        return new PairingStateHandle(value);
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            PairingNative.lc_spake_destroy(handle);
        }
        catch
        {
            // SafeHandle cleanup must not surface a native-loader error during
            // finalization or a best-effort Dispose call.
        }

        return true;
    }
}

/// <summary>
/// One-shot wrapper around the native SPAKE2 exchange. The native state is
/// consumed by Finish whether the peer message is valid or not.
/// </summary>
public sealed class PairingSpake : IDisposable
{
    public const int MessageBytes = 33;
    public const int PairingCodeBytes = 8;

    private readonly object gate = new();
    private readonly byte[] message;
    private PairingStateHandle? state;
    private bool consumed;
    private bool disposed;

    public PairingSpake(byte role, string code, Guid clientID, Guid hostID, Guid groupID)
    {
        if (role > 1 || !IsEightDigitCode(code))
        {
            throw new PairingCryptoException("Pairing input is invalid.");
        }

        byte[] password = Encoding.ASCII.GetBytes(code);
        byte[] idA = PairingCrypto.IdentityA(clientID);
        byte[] idB = PairingCrypto.IdentityB(hostID, groupID);
        byte[] messageOut = new byte[MessageBytes];
        IntPtr nativeState;
        try
        {
            nativeState = PairingNative.lc_spake_start(
                role,
                password,
                (nuint)password.Length,
                idA,
                (nuint)idA.Length,
                idB,
                (nuint)idB.Length,
                messageOut);
        }
        catch (DllNotFoundException)
        {
            throw new PairingCryptoException("Pairing backend is unavailable.");
        }
        catch (EntryPointNotFoundException)
        {
            throw new PairingCryptoException("Pairing backend is unavailable.");
        }
        catch (BadImageFormatException)
        {
            throw new PairingCryptoException("Pairing backend is unavailable.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(idA);
            CryptographicOperations.ZeroMemory(idB);
        }

        if (nativeState == IntPtr.Zero)
        {
            CryptographicOperations.ZeroMemory(messageOut);
            throw new PairingCryptoException("Pairing start failed.");
        }

        try
        {
            state = PairingStateHandle.Create(nativeState);
            message = messageOut.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(messageOut);
        }
    }

    /// <summary>
    /// Convenience overload for callers that hold the role as an integer.
    /// </summary>
    public PairingSpake(int role, string code, Guid clientID, Guid hostID, Guid groupID)
        : this(ValidateRole(role), code, clientID, hostID, groupID)
    {
    }

    /// <summary>
    /// Returns a copy of the 33-byte public SPAKE message.
    /// </summary>
    public byte[] Message => message.ToArray();

    /// <summary>
    /// Completes and consumes the native state. Calling this method again is
    /// rejected, including after an invalid peer message.
    /// </summary>
    public byte[] Finish(byte[]? peerMessage)
    {
        lock (gate)
        {
            if (disposed || consumed || state is null)
            {
                throw new PairingCryptoException("Pairing state is already consumed.");
            }

            consumed = true;
            PairingStateHandle nativeState = state;
            state = null;
            bool nativeConsumed = false;
            try
            {
                if (peerMessage is null || peerMessage.Length != MessageBytes)
                {
                    // The C ABI consumes a non-null state before validating the
                    // peer buffer. Passing null deliberately consumes it too.
                    try
                    {
                        _ = PairingNative.lc_spake_finish(
                            nativeState.DangerousGetHandle(),
                            null,
                            0,
                            null);
                        nativeConsumed = true;
                    }
                    catch (DllNotFoundException)
                    {
                        throw new PairingCryptoException("Pairing backend is unavailable.");
                    }
                    catch (EntryPointNotFoundException)
                    {
                        throw new PairingCryptoException("Pairing backend is unavailable.");
                    }
                    catch (BadImageFormatException)
                    {
                        throw new PairingCryptoException("Pairing backend is unavailable.");
                    }

                    throw new PairingCryptoException("Peer pairing message is invalid.");
                }

                byte[] nativeKey = new byte[PairingCrypto.KeyBytes];
                byte[] result;
                try
                {
                    int success;
                    try
                    {
                        success = PairingNative.lc_spake_finish(
                            nativeState.DangerousGetHandle(),
                            peerMessage,
                            (nuint)peerMessage.Length,
                            nativeKey);
                        nativeConsumed = true;
                    }
                    catch (DllNotFoundException)
                    {
                        throw new PairingCryptoException("Pairing backend is unavailable.");
                    }
                    catch (EntryPointNotFoundException)
                    {
                        throw new PairingCryptoException("Pairing backend is unavailable.");
                    }
                    catch (BadImageFormatException)
                    {
                        throw new PairingCryptoException("Pairing backend is unavailable.");
                    }

                    if (success != 1)
                    {
                        throw new PairingCryptoException("Pairing confirmation failed.");
                    }

                    result = nativeKey.ToArray();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nativeKey);
                }

                return result;
            }
            finally
            {
                if (nativeConsumed)
                {
                    // lc_spake_finish takes ownership and consumes the Box.
                    nativeState.SetHandleAsInvalid();
                }

                nativeState.Dispose();
            }
        }
    }

    public byte[] Finish(ReadOnlySpan<byte> peerMessage)
    {
        return Finish(peerMessage.ToArray());
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            consumed = true;
            PairingStateHandle? nativeState = state;
            state = null;
            nativeState?.Dispose();
        }
    }

    private static bool IsEightDigitCode(string? code)
    {
        if (code is null || code.Length != PairingCodeBytes)
        {
            return false;
        }

        foreach (char value in code)
        {
            if (value < '0' || value > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static byte ValidateRole(int role)
    {
        if (role is < 0 or > 1)
        {
            throw new PairingCryptoException("Pairing input is invalid.");
        }

        return (byte)role;
    }
}

/// <summary>
/// Pure managed cryptographic and transcript helpers shared by the Windows
/// client and the platform-neutral .NET interop tests.
/// </summary>
public static class PairingCrypto
{
    public const int KeyBytes = 32;
    public const int SaltBytes = 16;
    public const int GroupKeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int WrappedGroupBytes = NonceBytes + 16 + GroupKeyBytes + TagBytes;
    public const int TranscriptMessageBytes = 33;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] TranscriptPrefix = StrictUtf8.GetBytes("LightClip/pair/v2\0");
    private static readonly byte[] GroupKeyAadPrefix = StrictUtf8.GetBytes("LightClip/group-key/v2\0");

    /// <summary>
    /// Builds the exact pairing transcript:
    /// prefix, three RFC4122 UUIDs, message A, message B, and 16-byte salt.
    /// </summary>
    public static byte[] Transcript(
        Guid clientID,
        Guid hostID,
        Guid groupID,
        ReadOnlySpan<byte> messageA,
        ReadOnlySpan<byte> messageB,
        ReadOnlySpan<byte> salt)
    {
        ValidateLength(messageA, TranscriptMessageBytes);
        ValidateLength(messageB, TranscriptMessageBytes);
        ValidateLength(salt, SaltBytes);

        int length = TranscriptPrefix.Length + 16 + 16 + 16 + messageA.Length + messageB.Length + salt.Length;
        byte[] result = new byte[length];
        int offset = 0;
        TranscriptPrefix.CopyTo(result, offset);
        offset += TranscriptPrefix.Length;
        WriteGuidNetworkOrder(clientID, result.AsSpan(offset, 16));
        offset += 16;
        WriteGuidNetworkOrder(hostID, result.AsSpan(offset, 16));
        offset += 16;
        WriteGuidNetworkOrder(groupID, result.AsSpan(offset, 16));
        offset += 16;
        messageA.CopyTo(result.AsSpan(offset, messageA.Length));
        offset += messageA.Length;
        messageB.CopyTo(result.AsSpan(offset, messageB.Length));
        offset += messageB.Length;
        salt.CopyTo(result.AsSpan(offset, salt.Length));
        return result;
    }

    /// <summary>
    /// HKDF-SHA256(IKM=SPAKE key, salt=salt, info=transcript, L=32).
    /// </summary>
    public static byte[] DeriveKey(
        ReadOnlySpan<byte> spakeKey,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> transcript)
    {
        ValidateKey(spakeKey);
        ValidateLength(salt, SaltBytes);
        ValidateTranscript(transcript);

        byte[] ikm = spakeKey.ToArray();
        byte[] saltBytes = salt.ToArray();
        byte[] info = transcript.ToArray();
        byte[] prk;
        try
        {
            using (var extract = new HMACSHA256(saltBytes))
            {
                prk = extract.ComputeHash(ikm);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
            CryptographicOperations.ZeroMemory(saltBytes);
        }

        byte[] expandInput = new byte[info.Length + 1];
        info.CopyTo(expandInput, 0);
        expandInput[^1] = 1;
        try
        {
            using var expand = new HMACSHA256(prk);
            return expand.ComputeHash(expandInput);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
            CryptographicOperations.ZeroMemory(info);
            CryptographicOperations.ZeroMemory(expandInput);
        }
    }

    /// <summary>
    /// HMAC-SHA256(pairKey, UTF8(label + NUL) || transcript).
    /// </summary>
    public static byte[] Proof(
        ReadOnlySpan<byte> key,
        string label,
        ReadOnlySpan<byte> transcript)
    {
        ValidateKey(key);
        ValidateTranscript(transcript);
        ValidateLabel(label);

        byte[] labelBytes = StrictUtf8.GetBytes(label + "\0");
        byte[] input = new byte[labelBytes.Length + transcript.Length];
        labelBytes.CopyTo(input, 0);
        transcript.CopyTo(input.AsSpan(labelBytes.Length));
        byte[] keyBytes = key.ToArray();
        try
        {
            using var hmac = new HMACSHA256(keyBytes);
            return hmac.ComputeHash(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(labelBytes);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <summary>
    /// Constant-time verification with the expected proof first.
    /// </summary>
    public static bool VerifyProof(
        ReadOnlySpan<byte> expectedProof,
        ReadOnlySpan<byte> key,
        string label,
        ReadOnlySpan<byte> transcript)
    {
        if (expectedProof.Length != KeyBytes)
        {
            return false;
        }

        byte[] calculated = Proof(key, label, transcript);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedProof, calculated);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(calculated);
        }
    }

    /// <summary>
    /// Constant-time verification with the key and label first, for callers
    /// that mirror the Proof argument order.
    /// </summary>
    public static bool VerifyProof(
        ReadOnlySpan<byte> key,
        string label,
        ReadOnlySpan<byte> transcript,
        ReadOnlySpan<byte> expectedProof)
    {
        return VerifyProof(expectedProof, key, label, transcript);
    }

    /// <summary>
    /// Encrypts GroupUUID(16) || groupKey(32) with AES-256-GCM. The output is
    /// nonce(12) || ciphertext(48) || tag(16).
    /// </summary>
    public static byte[] WrapGroup(
        Guid groupID,
        ReadOnlySpan<byte> groupKey,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> transcript)
    {
        ValidateLength(groupKey, GroupKeyBytes);
        ValidateKey(key);
        ValidateTranscript(transcript);

        byte[] plaintext = new byte[16 + GroupKeyBytes];
        WriteGuidNetworkOrder(groupID, plaintext.AsSpan(0, 16));
        groupKey.CopyTo(plaintext.AsSpan(16, GroupKeyBytes));
        byte[] wrapped = new byte[WrappedGroupBytes];
        byte[] aad = BuildAad(GroupKeyAadPrefix, transcript);
        byte[] wrappingKey = key.ToArray();
        try
        {
            Span<byte> nonce = wrapped.AsSpan(0, NonceBytes);
            Span<byte> ciphertext = wrapped.AsSpan(NonceBytes, plaintext.Length);
            Span<byte> tag = wrapped.AsSpan(NonceBytes + plaintext.Length, TagBytes);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(wrappingKey, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            return wrapped;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    /// <summary>
    /// Decrypts and validates the expected group UUID, returning its 32-byte key.
    /// </summary>
    public static byte[] UnwrapGroup(
        Guid expectedGroupID,
        ReadOnlySpan<byte> sealedGroup,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> transcript)
    {
        ValidateLength(sealedGroup, WrappedGroupBytes);
        ValidateKey(key);
        ValidateTranscript(transcript);

        byte[] plaintext = new byte[16 + GroupKeyBytes];
        byte[] aad = BuildAad(GroupKeyAadPrefix, transcript);
        byte[] wrappingKey = key.ToArray();
        try
        {
            try
            {
                using var aes = new AesGcm(wrappingKey, TagBytes);
                aes.Decrypt(
                    sealedGroup[..NonceBytes],
                    sealedGroup.Slice(NonceBytes, plaintext.Length),
                    sealedGroup.Slice(NonceBytes + plaintext.Length, TagBytes),
                    plaintext,
                    aad);
            }
            catch (CryptographicException)
            {
                throw new PairingCryptoException("Wrapped group key is invalid.");
            }

            Guid actualGroupID = ReadGuidNetworkOrder(plaintext.AsSpan(0, 16));
            if (actualGroupID != expectedGroupID)
            {
                throw new PairingCryptoException("Wrapped group key is for another group.");
            }

            return plaintext.AsSpan(16, GroupKeyBytes).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    internal static byte[] IdentityA(Guid clientID)
    {
        string value = "LightClip/pair/v2/client/" + clientID.ToString("D").ToLowerInvariant();
        return StrictUtf8.GetBytes(value);
    }

    internal static byte[] IdentityB(Guid hostID, Guid groupID)
    {
        string value = "LightClip/pair/v2/host/" +
            hostID.ToString("D").ToLowerInvariant() + "/" +
            groupID.ToString("D").ToLowerInvariant();
        return StrictUtf8.GetBytes(value);
    }

    private static byte[] BuildAad(byte[] prefix, ReadOnlySpan<byte> transcript)
    {
        byte[] aad = new byte[prefix.Length + transcript.Length];
        prefix.CopyTo(aad, 0);
        transcript.CopyTo(aad.AsSpan(prefix.Length));
        return aad;
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        ValidateLength(key, KeyBytes);
    }

    private static void ValidateTranscript(ReadOnlySpan<byte> transcript)
    {
        int expected = TranscriptPrefix.Length + 16 + 16 + 16 + TranscriptMessageBytes + TranscriptMessageBytes + SaltBytes;
        ValidateLength(transcript, expected);
    }

    private static void ValidateLabel(string? label)
    {
        if (label is not ("server-confirm" or "client-confirm" or "stored"))
        {
            throw new PairingCryptoException("Pairing proof label is invalid.");
        }
    }

    private static void ValidateLength(ReadOnlySpan<byte> value, int expected)
    {
        if (value.Length != expected)
        {
            throw new PairingCryptoException("Pairing data length is invalid.");
        }
    }

    private static void WriteGuidNetworkOrder(Guid value, Span<byte> destination)
    {
        if (destination.Length < 16)
        {
            throw new PairingCryptoException("Pairing data length is invalid.");
        }

        Span<byte> legacy = stackalloc byte[16];
        _ = value.TryWriteBytes(legacy);
        destination[0] = legacy[3];
        destination[1] = legacy[2];
        destination[2] = legacy[1];
        destination[3] = legacy[0];
        destination[4] = legacy[5];
        destination[5] = legacy[4];
        destination[6] = legacy[7];
        destination[7] = legacy[6];
        legacy[8..].CopyTo(destination[8..]);
        CryptographicOperations.ZeroMemory(legacy);
    }

    private static Guid ReadGuidNetworkOrder(ReadOnlySpan<byte> source)
    {
        ValidateLength(source, 16);
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
        Guid value = new Guid(legacy);
        CryptographicOperations.ZeroMemory(legacy);
        return value;
    }
}
