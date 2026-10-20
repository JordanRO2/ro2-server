using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace RO2.Network.ProudNet;

/// <summary>
/// ProudNet encryption helper for RSA key exchange and AES encryption
/// </summary>
public class ProudNetCrypto : IDisposable
{
    /// <summary>Modulus size, in bits, of the RSA key the handshake offers the client.</summary>
    private const int HandshakeRsaKeyBits = 1024;

    /// <summary>Length of one AES-128 key, in bytes. The handshake carries the C2S key then the S2C key.</summary>
    private const int AesKeyBytes = 16;

    /// <summary>AES block size, in bytes; encrypted payloads are always a whole number of blocks.</summary>
    private const int AesBlockBytes = 16;

    /// <summary>Length of the CRC32 field in the encrypted payload trailer.</summary>
    private const int Crc32Bytes = 4;

    /// <summary>
    /// Length of the trailer closing every encrypted payload: the CRC32 of the data (4 bytes) followed by the
    /// zero-pad length (1 byte). Layout: <c>[data][zero pad][CRC32][padLen]</c>.
    /// </summary>
    private const int PaddingTrailerBytes = Crc32Bytes + 1;

    /// <summary>ASN.1 DER tag of an INTEGER.</summary>
    private const byte Asn1TagInteger = 0x02;

    /// <summary>ASN.1 DER tag of a SEQUENCE.</summary>
    private const byte Asn1TagSequence = 0x30;

    /// <summary>Largest DER length that fits the one-byte short form.</summary>
    private const int Asn1ShortFormMaxLength = 127;

    /// <summary>Largest DER length that fits the long form with one length byte.</summary>
    private const int Asn1LongFormOneByteMaxLength = 255;

    /// <summary>DER long-form length prefix announcing one following length byte.</summary>
    private const byte Asn1LongFormOneByte = 0x81;

    /// <summary>DER long-form length prefix announcing two following length bytes.</summary>
    private const byte Asn1LongFormTwoBytes = 0x82;

    /// <summary>High bit of a DER INTEGER's first byte; when set the value needs a leading zero to stay positive.</summary>
    private const byte Asn1SignBit = 0x80;

    /// <summary>Reflected CRC-32 (IEEE 802.3) polynomial.</summary>
    private const uint Crc32Polynomial = 0xEDB88320;

    /// <summary>Initial register value of the CRC-32; the final result is the bitwise complement.</summary>
    private const uint Crc32Seed = 0xFFFFFFFF;

    /// <summary>
    /// The handshake's RSA key, generated once per process on first use and shared by every session
    /// (S-H6). Generating a 1024-bit key per 0x2F cost a keygen per connection attempt, which an
    /// unauthenticated peer could repeat at will, and the replaced key was never disposed. The public half
    /// goes out in the clear to every client, so a per-session key bought no secrecy. Sessions never
    /// dispose it; it lives as long as the process.
    /// </summary>
    private sealed class SharedHandshakeKey(RSA rsa, byte[] publicKeyDer)
    {
        public RSA Rsa { get; } = rsa;
        public byte[] PublicKeyDer { get; } = publicKeyDer;
    }

    private static readonly Lazy<SharedHandshakeKey> SharedKey = new(() =>
    {
        var rsa = RSA.Create(HandshakeRsaKeyBits);
        return new SharedHandshakeKey(rsa, ExportPublicKeyDer(rsa));
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ILogger? _logger;
    private RSA? _rsa;
    private byte[]? _aesKey;      // C2S key (for decrypting client messages)
    private byte[]? _aesS2CKey;   // S2C key (for encrypting server messages)
    private byte[]? _aesIv;
    private byte[]? _rc4Key;      // RC4 key for fast-mode encryption (KEY #4, 64 bytes)
    private bool _disposed;

    /// <summary>
    /// RSA public key in DER format
    /// </summary>
    public byte[] PublicKeyDer { get; private set; } = [];

    /// <summary>
    /// Whether encryption is ready (session key received)
    /// </summary>
    public bool IsReady => _aesKey != null;

    /// <summary>
    /// Whether RC4 fast-mode encryption is available (KEY #4 received)
    /// </summary>
    public bool IsRc4Ready => _rc4Key != null;

    /// <param name="logger">Where decrypt/encrypt failures are logged (Debug); null keeps them silent.</param>
    public ProudNetCrypto(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attach this session to the process-wide RSA 1024-bit handshake key (generated on first use).
    /// Idempotent: a second call on the same instance changes nothing.
    /// </summary>
    public void GenerateKeyPair()
    {
        if (_rsa != null)
            return;

        var shared = SharedKey.Value;
        _rsa = shared.Rsa;
        PublicKeyDer = shared.PublicKeyDer;
    }

    /// <summary>
    /// Export the RSA public key in DER format (SubjectPublicKeyInfo)
    /// </summary>
    private static byte[] ExportPublicKeyDer(RSA rsa)
    {
        // Get RSA parameters
        var parameters = rsa.ExportParameters(false);

        // Build DER encoding manually for RSAPublicKey (PKCS#1)
        // Format:
        // SEQUENCE {
        //   INTEGER modulus
        //   INTEGER publicExponent
        // }

        var modulus = parameters.Modulus!;
        var exponent = parameters.Exponent!;

        // Encode modulus as INTEGER
        var modulusInt = EncodeInteger(modulus);

        // Encode exponent as INTEGER
        var exponentInt = EncodeInteger(exponent);

        // Build SEQUENCE
        var sequenceContent = new byte[modulusInt.Length + exponentInt.Length];
        modulusInt.CopyTo(sequenceContent, 0);
        exponentInt.CopyTo(sequenceContent, modulusInt.Length);

        return EncodeSequence(sequenceContent);
    }

    private static byte[] EncodeInteger(byte[] value)
    {
        // ASN.1 INTEGER encoding
        // If high bit is set, need to add leading 0x00

        var needsPadding = (value[0] & Asn1SignBit) != 0;
        var length = value.Length + (needsPadding ? 1 : 0);

        // Size calculation: 1 byte tag + length encoding + data
        // Short form (length <= 127): 1 byte for length
        // Long form (length > 127): 2 bytes for length (0x81 + length byte)
        var result = new byte[1 + (length > Asn1ShortFormMaxLength ? 2 : 1) + length];
        var offset = 0;

        // Tag: INTEGER
        result[offset++] = Asn1TagInteger;

        // Length
        if (length > Asn1ShortFormMaxLength)
        {
            result[offset++] = Asn1LongFormOneByte;
            result[offset++] = (byte)length;
        }
        else
        {
            result[offset++] = (byte)length;
        }

        // Value
        if (needsPadding)
            result[offset++] = 0x00;

        value.CopyTo(result, offset);

        return result;
    }

    private static byte[] EncodeSequence(byte[] content)
    {
        // ASN.1 SEQUENCE encoding
        var length = content.Length;

        int headerSize;
        if (length > Asn1ShortFormMaxLength)
            headerSize = length > Asn1LongFormOneByteMaxLength ? 4 : 3;
        else
            headerSize = 2;

        var result = new byte[headerSize + length];
        var offset = 0;

        // Tag: SEQUENCE
        result[offset++] = Asn1TagSequence;

        // Length
        if (length > Asn1LongFormOneByteMaxLength)
        {
            result[offset++] = Asn1LongFormTwoBytes;
            result[offset++] = (byte)(length >> 8);
            result[offset++] = (byte)length;
        }
        else if (length > Asn1ShortFormMaxLength)
        {
            result[offset++] = Asn1LongFormOneByte;
            result[offset++] = (byte)length;
        }
        else
        {
            result[offset++] = (byte)length;
        }

        content.CopyTo(result, offset);
        return result;
    }

    /// <summary>
    /// Decrypt the session key from the client's RSA-encrypted data
    /// </summary>
    /// <param name="encryptedData">RSA-encrypted session key (128 bytes)</param>
    /// <returns>True if decryption succeeded</returns>
    public bool DecryptSessionKey(ReadOnlySpan<byte> encryptedData)
    {
        // No key before 0x2F; and once a session key is installed it is final (S-H6: no re-key anytime).
        if (_rsa == null || _aesKey != null)
            return false;

        var data = encryptedData.ToArray();
        byte[]? decrypted = null;

        // One shared RSA object serves every session's handshake: serialise its private-key operations.
        lock (_rsa)
        {
            // ProudNet uses RSA-OAEP with SHA1.
            try
            {
                decrypted = _rsa.Decrypt(data, RSAEncryptionPadding.OaepSHA1);
            }
            catch
            {
                // Try OAEP with SHA256 as fallback
                try
                {
                    decrypted = _rsa.Decrypt(data, RSAEncryptionPadding.OaepSHA256);
                }
                catch
                {
                    // Try PKCS#1 v1.5 as last resort
                    try
                    {
                        decrypted = _rsa.Decrypt(data, RSAEncryptionPadding.Pkcs1);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        if (decrypted == null)
            return false;


        // Session keys and packet contents are never written anywhere: they used to go to stdout on every
        // packet (keys included), bypassing the logger, costing a hex string per packet even with logging off.
        // The decrypted data should contain:
        // - AES key for C2S (16 bytes) - bytes 0-15
        // - AES key for S2C (16 bytes) - bytes 16-31
        // ProudNet uses separate keys for each direction.

        if (decrypted.Length >= 2 * AesKeyBytes)
        {
            // First 16 bytes = C2S AES key (client encrypts, server decrypts)
            _aesKey = decrypted[..AesKeyBytes];
            // Next 16 bytes = S2C AES key (server encrypts, client decrypts)
            _aesS2CKey = decrypted[AesKeyBytes..(2 * AesKeyBytes)];
            _aesIv = new byte[AesBlockBytes];  // ECB mode doesn't use IV
            return true;
        }
        else if (decrypted.Length >= AesKeyBytes)
        {
            // Just one key - use same for both directions
            _aesKey = decrypted[..AesKeyBytes];
            _aesS2CKey = decrypted[..AesKeyBytes];  // Same key for S2C
            _aesIv = new byte[AesBlockBytes];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Set the session keys directly (for testing)
    /// </summary>
    public void SetSessionKey(byte[] c2sKey, byte[]? s2cKey = null, byte[]? iv = null)
    {
        _aesKey = c2sKey;
        _aesS2CKey = s2cKey ?? c2sKey;  // Use same key if S2C not provided
        _aesIv = iv ?? new byte[AesBlockBytes];
    }

    /// <summary>
    /// Set the S2C key separately (extracted from encrypted extra data in 0x05 message)
    /// </summary>
    public void SetS2CKey(byte[] s2cKey)
    {
        _aesS2CKey = s2cKey;
    }

    /// <summary>
    /// Set the RC4 key for fast-mode encryption (KEY #4, 64 bytes)
    /// </summary>
    public void SetRc4Key(byte[] rc4Key)
    {
        _rc4Key = (byte[])rc4Key.Clone();
    }

    /// <summary>
    /// Decrypt data using RC4 stream cipher (fast-mode, flags1=0x02).
    /// ProudNet re-initializes the RC4 state for each message (fresh keystream per packet).
    /// </summary>
    public byte[]? Rc4Decrypt(ReadOnlySpan<byte> encryptedData)
    {
        if (_rc4Key == null)
            return null;


        var result = Rc4Transform(_rc4Key, encryptedData);

        return result;
    }

    /// <summary>
    /// ProudNet's modified RC4 stream cipher.
    /// Uses S-box size = key length (typically 64), not the standard 256.
    /// </summary>
    private static byte[] Rc4Transform(byte[] key, ReadOnlySpan<byte> data)
    {
        int n = key.Length; // S-box size = key length (64 for 512-bit)

        // KSA: initialize S-box of size N (not 256)
        var s = new byte[n];
        for (int i = 0; i < n; i++)
            s[i] = (byte)i;

        int j = 0;
        for (int i = 0; i < n; i++)
        {
            j = (j + s[i] + key[i % n]) % n;
            (s[i], s[j]) = (s[j], s[i]);
        }

        // PRGA: generate keystream with mod N
        var output = new byte[data.Length];
        int x = 0, y = 0;
        for (int k = 0; k < data.Length; k++)
        {
            x = (x + 1) % n;
            y = (y + s[x]) % n;
            (s[x], s[y]) = (s[y], s[x]);
            byte keystreamByte = s[(s[x] + s[y]) % n];
            output[k] = (byte)(data[k] ^ keystreamByte);
        }

        return output;
    }

    /// <summary>
    /// Decrypt data using the session AES key
    /// ProudNet uses AES-128 in ECB mode.
    /// </summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> encryptedData)
    {
        if (_aesKey == null)
            return null;


        try
        {
            using var aes = Aes.Create();
            aes.Key = _aesKey;
            aes.Mode = CipherMode.ECB;  // ProudNet uses ECB, not CBC
            aes.Padding = PaddingMode.None;  // Handle padding manually

            using var decryptor = aes.CreateDecryptor();
            var result = decryptor.TransformFinalBlock(encryptedData.ToArray(), 0, encryptedData.Length);
            return result;
        }
        catch (Exception ex)
        {
            // Debug, one line, no stack: a peer can trigger this per packet with a bad ciphertext length
            // (the session logs the drop, throttled).
            _logger?.LogDebug("Crypto.Decrypt failed: {Reason}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// <see cref="Decrypt"/> plus the integrity check the sender's padding carries: the last byte is the
    /// zero-pad length and the 4 bytes before it are the CRC32 of the data that precedes the zeros
    /// (layout of <see cref="Encrypt"/>). Returns null when the pad length is impossible or the CRC does
    /// not match, so a flipped or swapped ciphertext block cannot reach dispatch (S-H5). The returned
    /// buffer is the whole decrypted block, trailer included, exactly as <see cref="Decrypt"/> returns it.
    /// </summary>
    /// <param name="encryptedData">The AES ciphertext.</param>
    /// <param name="plainLength">Bytes of real data in the block (before the zero pad and trailer).</param>
    public byte[]? DecryptVerified(ReadOnlySpan<byte> encryptedData, out int plainLength)
    {
        plainLength = 0;
        var block = Decrypt(encryptedData);
        if (block == null || block.Length < PaddingTrailerBytes)
            return null;

        int padLen = block[^1];
        int dataLen = block.Length - PaddingTrailerBytes - padLen;
        if (dataLen < 0)
            return null;

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(block.Length - PaddingTrailerBytes, Crc32Bytes));
        if (CalculateCrc32(block.AsSpan(0, dataLen)) != expected)
            return null;

        plainLength = dataLen;
        return block;
    }

    /// <summary>
    /// Encrypt data using the session AES key (KEY #3)
    /// ProudNet uses AES-128 in ECB mode with CRC32 checksum padding.
    /// Format: [data:N][zeros:P][CRC32:4][padlen:1] = 32 bytes (or next 16-byte boundary)
    /// Key #3 is used for all RMI traffic (both C2S and S2C).
    /// </summary>
    public byte[]? Encrypt(ReadOnlySpan<byte> plainData)
    {
        // Key #3 is used for both C2S and S2C.
        var key = _aesKey;
        if (key == null)
            return null;


        // Calculate padded length: [data:N][zeros:P][CRC32:4][padlen:1]
        // Total must be multiple of 16 and >= data + 5
        var minLength = plainData.Length + PaddingTrailerBytes; // data + CRC32(4) + padlen(1)
        var paddedLength = ((minLength + AesBlockBytes - 1) / AesBlockBytes) * AesBlockBytes; // Round up to next block

        // Calculate padding zeros needed
        var padLen = paddedLength - plainData.Length - PaddingTrailerBytes; // zeros between data and CRC32

        // Build padded block: [data][zeros][CRC32][padlen]
        var dataToEncrypt = new byte[paddedLength];
        plainData.CopyTo(dataToEncrypt);
        // Zeros are already there (default byte array value)

        // Calculate CRC32 of the plaintext data
        var crc = CalculateCrc32(plainData);
        BitConverter.GetBytes(crc).CopyTo(dataToEncrypt.AsSpan(paddedLength - PaddingTrailerBytes));

        // Padding length byte at the end
        dataToEncrypt[paddedLength - 1] = (byte)padLen;


        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;  // ProudNet uses ECB, not CBC
            aes.Padding = PaddingMode.None;

            using var encryptor = aes.CreateEncryptor();
            var result = encryptor.TransformFinalBlock(dataToEncrypt, 0, dataToEncrypt.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Crypto.Encrypt failed: {Reason}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Calculate CRC32 checksum (used in ProudNet padding)
    /// </summary>
    private static uint CalculateCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = Crc32Seed;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ Crc32Polynomial;
                else
                    crc >>= 1;
            }
        }
        return ~crc;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The RSA key is the process-wide shared one: this session only drops its reference.
        _rsa = null;

        if (_aesKey != null)
        {
            CryptographicOperations.ZeroMemory(_aesKey);
            _aesKey = null;
        }

        if (_aesS2CKey != null)
        {
            CryptographicOperations.ZeroMemory(_aesS2CKey);
            _aesS2CKey = null;
        }

        if (_aesIv != null)
        {
            CryptographicOperations.ZeroMemory(_aesIv);
            _aesIv = null;
        }

        if (_rc4Key != null)
        {
            CryptographicOperations.ZeroMemory(_rc4Key);
            _rc4Key = null;
        }
    }
}
