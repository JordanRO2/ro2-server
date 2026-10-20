using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Configuration;
using RO2.Server.Login.Services;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>
/// Handler for RMI 12001 - Version Check
/// Client sends this first to verify compatible version
/// </summary>
public class VersionCheckHandler : LoginRmiHandlerBase<VersionCheckHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.VersionCheck;
    public override string Name => "VersionCheck";

    /// <summary>Declared message size (innerSize) of the 12001 request: header, 4 unused bytes, 8 build bytes.</summary>
    private const int VersionCheckLength = 16;

    // Request layout (little-endian): [innerSize:u16 @0][msgTag:u16 @2][unused:4 @4][configWord:u32 @8][configBytes:4 @12].
    private const int MsgTagOffset = 2;
    private const int ConfigWordOffset = 8;
    private const int ConfigBytesOffset = 12;
    private const int ConfigBytesLength = 4;

    /// <summary>Message tag of the 12001 request, as the u16 at body offset 2 (RMI 12001 + 40000; bytes 21 cb).</summary>
    private const ushort MsgTagVersionCheck = 0xCB21; // = 52001

    /// <summary>Expected u16 innerSize at body offset 0: the whole declared message, 16 bytes (bytes 10 00).</summary>
    private const ushort ExpectedInnerSize = VersionCheckLength;

    /// <summary>Expected u32 at body offset 8 (bytes 01 00 00 00), the first half of the build fingerprint.</summary>
    private const uint ExpectedConfigWord = 1;

    /// <summary>Size of the 12501 answer body: <c>[timestamp:8][result:2]</c>.</summary>
    private const int ResultBodySize = 10;

    /// <summary>Offset in the 12501 body of the u16 result word (after the 8-byte timestamp).</summary>
    private const int ResultCodeOffset = 8;

    /// <summary>Most bytes of a client body written into a log line (S-H11).</summary>
    private const int MaxLoggedBytes = 64;

    /// <summary>
    /// Non-zero result word of the 12501 answer to a request that is not the supported client's. The client
    /// treats any non-zero word as VersionFailed (state 4, the failure path it already has); only its being
    /// non-zero is relied on.
    /// </summary>
    private const ushort VersionFailedResult = 1;

    /// <summary>Expected body[12]: first of the four client configuration bytes (0x01).</summary>
    private const byte ExpectedConfigByte0 = 0x01;

    /// <summary>Expected body[13]: second of the four client configuration bytes (0x07).</summary>
    private const byte ExpectedConfigByte1 = 0x07;

    /// <summary>Expected body[14]: third of the four client configuration bytes (0x02).</summary>
    private const byte ExpectedConfigByte2 = 0x02;

    /// <summary>Expected body[15]: fourth of the four client configuration bytes (0x25).</summary>
    private const byte ExpectedConfigByte3 = 0x25;

    /// <summary>
    /// body[12..16): the second half of the client's VERSION fingerprint, the four configuration bytes
    /// <see cref="ExpectedConfigByte0"/> to <see cref="ExpectedConfigByte3"/> (01 07 02 25). They are compared as one
    /// value, not as four separate fields. Together with <see cref="ExpectedConfigWord"/> they ARE the version
    /// identity, so a client UPDATE that bumps those config fields changes them, and these constants are then
    /// re-pinned to the new client's values (read body+8..15 of its 12001).
    /// </summary>
    private static ReadOnlySpan<byte> ExpectedConfigBytes =>
        [ExpectedConfigByte0, ExpectedConfigByte1, ExpectedConfigByte2, ExpectedConfigByte3];

    private readonly ServerConfiguration _config;

    public VersionCheckHandler(IServiceScopeFactory scopeFactory, ILogger<VersionCheckHandler> logger, ServerConfiguration config)
        : base(scopeFactory, logger)
    {
        _config = config;
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogDebug("Session {SessionId}: RMI 12001 (VersionCheck) received ({Size} bytes): {Hex}",
            session.SessionId, payload.Length, LoggedHex(payload.Span));

        // Request: [u16 innerSize=16][u16 msgTag=52001][u32 not a field][u32 @8][u32 @12].
        // Bytes 4..7 are not a field: the client never writes there, so they carry arbitrary data. The client's own
        // innerSize says the message is 16 bytes (10 00 21 cb | .. .. .. .. | 01 00 00 00 | 01 07 02 25); some
        // builds append a build tag after them and send 27 bytes. So only the 16-byte prefix the client declares is
        // matched; anything else at 0..3 or 8..15 is a different build.
        // S-H10: the outcome is remembered per connection and 12002 is refused until it is OK (LoginHandler); a
        // mismatch is answered on the client's own failure path.
        var body = payload.Span;
        var (expectedWord, expectedConfigBytes) = ParseClientVersion(_config.Game.ClientVersion);
        var versionOk = IsSupportedClientVersion(body, expectedWord, expectedConfigBytes);
        LoginSessionState.For(session).VersionOk = versionOk;
        if (!versionOk)
        {
            Logger.LogWarning("Session {SessionId}: 12001 does not match the b303 client ({Hex}); answering VersionFailed "
                + "and refusing 12002 on this connection", session.SessionId, LoggedHex(body));
        }

        // 1108 is already sent batched with 0x0A in ClientConnectInfoHandler.
        // Payload format (10 bytes): [Timestamp:8][ResultCode:2]
        // ResultCode 0 = VersionOk (state 5), non-zero = VersionFailed (state 4)
        await session.SendRmiAsync(LoginS2COpcodes.VersionCheckResult, writer =>
        {
            // All zeros = version OK; a non-zero result code = VersionFailed (state 4)
            writer.WriteBytes(BuildVersionResult12501(versionOk));
        });

        Logger.LogInformation("Session {SessionId}: Version check response sent (RMI 12501, 10 bytes payload)",
            session.SessionId);
    }

    /// <summary>
    /// True when <paramref name="body"/> carries the supported client's version message: the 16-byte fingerprint
    /// the client declares via its innerSize (header at 0..3, build words at 8..15; bytes 4..7 are not a field).
    /// Matched on that 16-byte PREFIX, not an exact length: some builds append a build tag and send 27 bytes
    /// (see HandleAsync), others send exactly 16 - both carry the same fingerprint. A body shorter than 16, a
    /// wrong header or wrong build words still fail.
    /// </summary>
    /// <summary>
    /// Parses the configured client version string <c>word.b0.b1.b2.b3</c> (as the client's version display reads
    /// it, e.g. <c>1.1.7.2.37</c>) into the u32 build word and the four config bytes the 12001 fingerprint carries.
    /// </summary>
    internal static (uint Word, byte[] ConfigBytes) ParseClientVersion(string version)
    {
        var p = version.Split('.');
        return (uint.Parse(p[0]),
            new[] { byte.Parse(p[1]), byte.Parse(p[2]), byte.Parse(p[3]), byte.Parse(p[4]) });
    }

    public static bool IsSupportedClientVersion(ReadOnlySpan<byte> body)
        => IsSupportedClientVersion(body, ExpectedConfigWord, ExpectedConfigBytes);

    /// <summary>
    /// As <see cref="IsSupportedClientVersion(ReadOnlySpan{byte})"/>, but comparing the build words against a
    /// CONFIGURED accepted version (<paramref name="expectedWord"/> at body+8, <paramref name="expectedConfigBytes"/>
    /// at body+12), so accepting a new client is a config change rather than a code change. The protocol header
    /// (innerSize, msgTag) is fixed and always checked.
    /// </summary>
    public static bool IsSupportedClientVersion(ReadOnlySpan<byte> body, uint expectedWord, ReadOnlySpan<byte> expectedConfigBytes)
        => body.Length >= VersionCheckLength
           && BinaryPrimitives.ReadUInt16LittleEndian(body) == ExpectedInnerSize
           && BinaryPrimitives.ReadUInt16LittleEndian(body[MsgTagOffset..]) == MsgTagVersionCheck
           && BinaryPrimitives.ReadUInt32LittleEndian(body[ConfigWordOffset..]) == expectedWord
           && body.Slice(ConfigBytesOffset, ConfigBytesLength).SequenceEqual(expectedConfigBytes);

    /// <summary>The 12501 body, <c>[timestamp:8][result:2]</c>: all zeros for OK (unchanged), a non-zero result
    /// word for a mismatch (see <see cref="VersionFailedResult"/>).</summary>
    public static byte[] BuildVersionResult12501(bool versionOk)
    {
        var result = new byte[ResultBodySize];
        if (!versionOk)
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(ResultCodeOffset), VersionFailedResult);
        return result;
    }

    private static string LoggedHex(ReadOnlySpan<byte> body)
        => body.Length <= MaxLoggedBytes
            ? Convert.ToHexString(body)
            : Convert.ToHexString(body[..MaxLoggedBytes]) + "...(" + body.Length + " bytes)";
}
