using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;

namespace RO2.Network.Handlers.Shared;

/// <summary>
/// Shared handler for ProudNet 0x05 - Encrypted Session Key.
/// Decrypts RSA session key and extracts RC4 fast-encryption key.
/// </summary>
/// <remarks>
/// Payload layout (after the core opcode byte):
/// <code>
/// +0   u8      key type (read, not used)
/// +1   u16 LE  length N of the RSA-encrypted session key
/// +3   N bytes RSA-encrypted session key
/// +3+N u8      (not read)
/// +4+N u8      length M of the session-key-encrypted extra block
/// +5+N M bytes extra block; decrypts to at least 64 bytes, the first 64 being the RC4 key
/// </code>
/// </remarks>
public abstract class EncryptedSessionKeyHandlerBase<THandler> : CoreHandlerBase<THandler>
    where THandler : class
{
    /// <summary>Size of the fixed header: key type (1) + encrypted-key length (2).</summary>
    private const int KeyHeaderLength = 3;

    /// <summary>Offset of the little-endian u16 encrypted-key length.</summary>
    private const int KeyLengthOffset = 1;

    /// <summary>Size of the extra-block header: one unread byte + the length byte.</summary>
    private const int ExtraHeaderLength = 2;

    /// <summary>Offset, within the extra-block header, of the length byte.</summary>
    private const int ExtraLengthOffset = 1;

    /// <summary>Smallest encrypted extra block that is worth decrypting.</summary>
    private const int MinExtraEncryptedLength = 16;

    /// <summary>Length of the RC4 fast-encryption key taken from the decrypted extra block.</summary>
    private const int Rc4KeyLength = 64;

    public override ProudCoreOpCode Opcode => ProudCoreOpCode.EncryptedSessionKey;
    public override string Name => "EncryptedSessionKey";

    protected EncryptedSessionKeyHandlerBase(ILogger<THandler> logger) : base(logger) { }

    protected override async Task HandleCoreAsync(
        ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // 0x05 answers the PeerAccept that 0x2F produced, once. In any other state it is out of order (a
        // key before the RSA public key, or a second key re-keying a live session): hard disconnect (S-H6).
        if (session.Handshake != HandshakeState.WaitingForSessionKey)
        {
            session.AbortHandshake($"EncryptedSessionKey (0x05) in handshake state {session.Handshake}");
            return;
        }

        Logger.LogInformation("Session {SessionId}: EncryptedSessionKey ({Size} bytes)",
            session.SessionId, payload.Length);

        var keyInstalled = false;

        if (payload.Length >= KeyHeaderLength)
        {
            var keyType = payload.Span[0];
            var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Span.Slice(KeyLengthOffset));

            if (payload.Length >= KeyHeaderLength + keyLen)
            {
                var encryptedKey = payload.Slice(KeyHeaderLength, keyLen);
                var extraOffset = KeyHeaderLength + keyLen;
                byte[]? extraEncryptedData = null;

                if (payload.Length > extraOffset + ExtraHeaderLength)
                {
                    var extraData = payload.Slice(extraOffset);
                    var extraDataLen = extraData.Span[ExtraLengthOffset];
                    if (extraData.Length >= ExtraHeaderLength + extraDataLen)
                        extraEncryptedData = extraData.Slice(ExtraHeaderLength, extraDataLen).ToArray();
                }

                if (session.Crypto.DecryptSessionKey(encryptedKey.Span))
                {
                    keyInstalled = true;
                    Logger.LogInformation("Session {SessionId}: Session key decrypted", session.SessionId);

                    if (extraEncryptedData != null && extraEncryptedData.Length >= MinExtraEncryptedLength)
                    {
                        var decryptedExtra = session.Crypto.Decrypt(extraEncryptedData);
                        if (decryptedExtra != null && decryptedExtra.Length >= Rc4KeyLength)
                        {
                            var rc4Key = decryptedExtra[..Rc4KeyLength];
                            Logger.LogDebug("Session {SessionId}: RC4 key extracted ({KeyLength} bytes)",
                                session.SessionId, Rc4KeyLength);
                            session.Crypto.SetRc4Key(rc4Key);
                        }
                    }
                }
                else
                {
                    Logger.LogWarning("Session {SessionId}: Failed to decrypt session key", session.SessionId);
                }
            }
        }

        // No key, no session: acknowledging a key that did not decrypt would let the peer carry on into
        // the connected state with nothing to encrypt its RMIs with.
        if (!keyInstalled)
        {
            session.AbortHandshake("session key missing or not decryptable");
            return;
        }

        await session.SendRawFrameAsync(new byte[] { (byte)ProudCoreOpCode.SessionKeySuccess });
        session.Handshake = HandshakeState.WaitingForConnectionRequest;
    }
}
