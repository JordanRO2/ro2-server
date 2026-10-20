using System.Runtime.CompilerServices;
using RO2.Application.Configuration;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Services;

/// <summary>
/// Shared login service for common operations
/// </summary>
/// <remarks>
/// There is no 12505 sender here, on purpose. The client's Login stub reads 12505 as one compact-prefixed
/// ByteArray and forwards it to its handler, so a body that starts with a plain count byte would have that
/// first byte read as a CompactLen width. The server therefore never sends 12505: the character list travels
/// on Zone 2307 OnCharacterListACK (ZoneAuthHandler / ZoneClientInitHandler).
/// </remarks>
public class LoginService
{
    /// <summary>
    /// Maximum length of a client-supplied string in a log line, and the control characters stripped from it
    /// (S-H11): a username with CR/LF would otherwise forge whole log lines.
    /// </summary>
    public const int MaxLoggedClientStringLength = 64;

    // ---- 12502 LoginResult body ----

    /// <summary>Characters in the 12502 account-name field (a wchar_t[37], terminator included).</summary>
    private const int AccountNameChars = 37;

    /// <summary>Bytes of the 12502 account-name field: <see cref="AccountNameChars"/> UTF-16LE characters.</summary>
    private const int AccountNameBytes = AccountNameChars * sizeof(char);

    /// <summary>Length of the 12502 body: u16 code + u32 account id + the name field = 80 bytes.</summary>
    private const int LoginResultLength = sizeof(ushort) + sizeof(uint) + AccountNameBytes;

    // ---- 12503 ServerGroupList: 32-bit server / channel id layout ----

    /// <summary>Bit position of the instance counter in an id ([31-20]).</summary>
    private const int IdInstanceShift = 20;

    /// <summary>Bit position of the type field in an id ([19-14]).</summary>
    private const int IdTypeShift = 14;

    /// <summary>Bit position of the slot field in an id ([13-6]).</summary>
    private const int IdSlotShift = 6;

    /// <summary>Id type of a server group.</summary>
    private const int IdTypeServer = 0x04;

    /// <summary>Id type of a channel.</summary>
    private const int IdTypeChannel = 0x05;

    /// <summary>Slot of a server group in its id (channels use 1..N).</summary>
    private const int ServerSlot = 0;

    /// <summary>Server groups listed: this server advertises exactly one.</summary>
    private const ushort ServerGroupCount = 1;

    /// <summary>Instance counter of channel N is this base + N.</summary>
    private const int ChannelInstanceBase = 100;

    /// <summary>Channel entry flag: the channel is online.</summary>
    private const byte ChannelFlagOnline = 0x01;

    /// <summary>Channel load indicator, range 0..3; 2 = medium.</summary>
    private const byte ChannelLoadMedium = 0x02;

    /// <summary>
    /// <paramref name="value"/> made safe to write into a log line: control characters (CR, LF, tab, ESC, NEL)
    /// and the Unicode line/paragraph separators are removed and the rest is cut to
    /// <paramref name="maxLength"/> characters. The wire data is never altered, only what is logged.
    /// </summary>
    public static string SanitizeForLog(string? value, int maxLength = MaxLoggedClientStringLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new System.Text.StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (char.IsControl(c) || c is '\u2028' or '\u2029') continue;
            if (sb.Length >= maxLength) break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Send login result packet (RMI 12502)
    /// Wire format: [ResponseCode:2][AccountId:4][Username+Data:74]
    /// The 74-byte blob holds the username as PLAINTEXT UTF-16LE + null padding. It is NOT XOR-encoded.
    /// WriteWideStringFixed below writes that format.
    /// </summary>
    public static async Task SendLoginResultAsync(
        ProudSession session,
        ushort responseCode,
        uint accountId = 0,
        string? username = null)
    {
        await session.SendRmiAsync(LoginS2COpcodes.LoginResult,
            writer => writer.WriteBytes(BuildLoginResult12502(responseCode, accountId, username)));
    }

    /// <summary>
    /// The 12502 body: <c>[u16 code][u32 accountId][wchar_t[37] name]</c>, 80 bytes.
    /// </summary>
    /// <remarks>
    /// The name field is the name the client submitted, whatever the code, when one is supplied; otherwise it
    /// is zero-filled. The bytes after the name's terminator are zeros.
    /// </remarks>
    internal static byte[] BuildLoginResult12502(ushort responseCode, uint accountId, string? username)
    {
        using var writer = new PacketWriter(LoginResultLength);
        writer.WriteUInt16(responseCode);
        writer.WriteUInt32(accountId);
        if (username != null)
            writer.WriteWideStringFixed(username, AccountNameChars); // wchar_t[37] = 74 bytes
        else
            writer.WriteZeros(AccountNameBytes);
        return writer.ToArray();
    }

    /// <summary>
    /// Send ServerGroupList (RMI 12503) with CMessage length prefix.
    ///
    /// ID bit structure (32 bits):
    ///   [31-20] Instance counter
    ///   [19-14] Type: 0x04=Server, 0x05=Channel
    ///   [13-6]  Slot: 0 for servers, 1-N for channels
    ///   [5-0]   Parent: server index (channels reference their parent server)
    ///
    /// Wire format:
    ///   [data_length:4][server_count:2]
    ///   per server: [id:4][name:wstring]
    ///   [channel_count:2]
    ///   per channel: [id:4][flag:1][name:wstring][ip:wstring][port:2][load:1]
    /// </summary>
    public static async Task SendServerGroupListAsync(
        ProudSession session,
        WorldConfig world,
        string zoneHost,
        ushort zonePort)
    {
        await session.SendRmiAsync(LoginS2COpcodes.ServerGroupList, writer =>
        {
            // CMessage data_length prefix
            var lengthPos = writer.Reserve(sizeof(uint));
            var dataStart = writer.Length;

            // Server ID: (instance << 20) | (type << 14) | (slot << 6) | index
            // Type 0x04 = server group, Type 0x05 = channel
            var serverId = (uint)((world.ServerInstance << IdInstanceShift) | (IdTypeServer << IdTypeShift) | (ServerSlot << IdSlotShift) | world.ServerIndex);

            // 1 server
            writer.WriteUInt16(ServerGroupCount);
            writer.WriteUInt32(serverId);
            writer.WriteUnicodeString(world.ServerName);

            // N channels
            writer.WriteUInt16((ushort)world.ChannelCount);

            for (int ch = 1; ch <= world.ChannelCount; ch++)
            {
                // Channel ID: (instance << 20) | (0x05 << 14) | (slot << 6) | parent
                int channelInstance = ChannelInstanceBase + ch;
                var channelId = (uint)((channelInstance << IdInstanceShift) | (IdTypeChannel << IdTypeShift) | (ch << IdSlotShift) | world.ServerIndex);

                writer.WriteUInt32(channelId);
                writer.WriteByte(ChannelFlagOnline);
                writer.WriteUnicodeString($"Channel {ch}");
                writer.WriteUnicodeString(zoneHost);
                writer.WriteUInt16(zonePort);
                writer.WriteByte(ChannelLoadMedium);
            }

            // Fill CMessage data_length
            writer.WriteAt(lengthPos, (uint)(writer.Length - dataStart));
        });
    }
}

/// <summary>
/// The login-flow gates of ONE Login connection, tied to the session object so they go away with it (S-H4,
/// S-H10). Handlers run one at a time per connection (the receive loop awaits each), so plain volatile
/// flags are enough.
/// </summary>
public sealed class LoginSessionState
{
    private static readonly ConditionalWeakTable<ProudSession, LoginSessionState> All = new();

    /// <summary>The state of <paramref name="session"/>, created all-closed on first use.</summary>
    public static LoginSessionState For(ProudSession session) => All.GetOrCreateValue(session);

    /// <summary>12001 matched the b303 client: 12002 is refused until this is set (S-H10).</summary>
    public volatile bool VersionOk;

    /// <summary>The account's PIN was verified (12508 = 2) or just created (12508 = 0) on this connection:
    /// 12005 is refused until this is set (S-H4).</summary>
    public volatile bool PinVerified;
}
