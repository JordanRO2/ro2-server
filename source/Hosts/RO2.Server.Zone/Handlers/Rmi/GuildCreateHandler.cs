using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1426 ReqGuildCreate — create a guild with real state.
/// C2S: [u32 len-4][guild name UTF-16 @+4, fixed field, null-terminated][emblem/intro...].
/// The name lives at a fixed offset with no length prefix.
/// After the wchar[50] name comes a WStr16 notice at +104 — by default the client's table string
/// 2000000112 "Guild always welcomes you!" — which is kept:
/// the new guild's next 2559 carries exactly that notice.
/// </summary>
public class GuildCreateHandler : ZoneRmiHandlerBase<GuildCreateHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildCreate;
    public override string Name => "GuildCreate";

    public GuildCreateHandler(IServiceScopeFactory s, ILogger<GuildCreateHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Request offset of the guild name field (right after the u32 length prefix).</summary>
    private const int NameOff = 4;

    /// <summary>Request offset of the WStr16 notice: [u16 char count][UTF-16 chars], after the wchar[50] name field.</summary>
    private const int NoticeOff = 104;

    /// <summary>Size of the u16 char count that prefixes a WStr16.</summary>
    private const int CountPrefixBytes = 2;

    /// <summary>Bytes per UTF-16 code unit.</summary>
    private const int WideCharBytes = 2;

    /// <summary>Smallest request that can hold a name: the length prefix plus one UTF-16 character.</summary>
    private const int MinNamedRequestLength = NameOff + WideCharBytes;

    /// <summary>2561 result code for a successful creation.</summary>
    private const ushort ResultSuccess = 0;

    /// <summary>Config_Def type 2, Memo "길드 생성에 필요한 금액" (amount required to create a guild), Value
    /// 20000 — the amount charged per creation (see <see cref="BuildAnsGuildCreate"/>);
    /// string_ui 7883 announces it in the name dialog.</summary>
    private const ushort ConfigGuildCreateFee = 2;

    /// <summary>Result code for a requester already in a guild.</summary>
    private const ushort ResultAlreadyInGuild = 1;

    /// <summary>
    /// No dedicated 2561 refusal code for an unpaid tax is known, and the guild creation refusals string_ui lists
    /// (7850-7858) carry no zeny message, so the client may check the wallet before it sends 1426. It
    /// only has to be non-zero, so the client does not treat the guild as created; the server must still
    /// refuse, since a modified client can send 1426 regardless.
    /// </summary>
    private const ushort ResultNotEnoughZeny = 2;

    /// <summary>
    /// The client has a message for a name already in use among the creation refusals
    /// (string_ui 7855 "That guild name already exists."). 5 is what this server answers for a taken name on 1585
    /// (2854 code 5, see <see cref="GuildRenameRules.NameTaken"/>).
    /// </summary>
    private const ushort ResultNameTaken = GuildRenameRules.NameTaken;

    /// <summary>
    /// The client refuses an empty name itself (string_ui 7858 "Did not enter the guild name."), so only a modified
    /// client gets here. It is the code this server gives 1585 for a name too short to use
    /// (<see cref="GuildRenameRules.NameNotUsable"/>).
    /// </summary>
    private const ushort ResultNameNotUsable = GuildRenameRules.NameNotUsable;

    /// <summary>The creation tax from Config_Def type 2; 0 (no tax) when the row is absent or unparseable,
    /// rather than a number made up here.</summary>
    private static long CreationFee(GameDataStore data)
    {
        var row = data.ConfigDef.FirstOrDefault(c => c.type == ConfigGuildCreateFee);
        return row != null && long.TryParse(row.Value, System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var fee) && fee > 0
            ? fee : 0;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);

        var span = payload.Span;
        var guildName = span.Length >= MinNamedRequestLength ? GuildPackets.ReadWideNameAt(span, NameOff, GuildNameMaxChars) : string.Empty;
        var notice = ReadNotice(span);

        var data = services.GetRequiredService<GameDataStore>();
        var fee = CreationFee(data);

        var existing = GuildService.Instance.GetByCharId(player.CharacterId);
        Guild? guild = null;
        ushort result = ResultSuccess;
        if (existing != null)
        {
            // Already in a guild: refused with code 1.
            result = ResultAlreadyInGuild;
        }
        else if (string.IsNullOrWhiteSpace(guildName))
        {
            result = ResultNameNotUsable;
        }
        else if (player.Gold < fee)
        {
            result = ResultNotEnoughZeny;
        }
        else if (!player.TryDebit(fee))
        {
            // The fee is taken here, atomically, before the guild exists; the branches below that end without a
            // guild hand it back.
            result = ResultNotEnoughZeny;
        }
        else
        {
            // The name must be free, as 1585/1586 require of a rename (2854 code 5: case-insensitive, any
            // registered guild). CreateAsync checks it and registers the guild under the same gate that
            // 1586's GuildService.RenameAsync checks and sets under, so neither two 1426s nor a 1426 and a
            // 1586 for the same name can both succeed.
            var (created, refusal) = await GuildService.Instance.CreateAsync(
                player.CharacterId, player.Name, player.Level, player.Job, guildName);
            if (refusal == GuildService.CreateRefusal.NameTaken) { result = ResultNameTaken; player.Credit(fee); }
            else if (created == null) { result = ResultAlreadyInGuild; player.Credit(fee); }   // joined another guild meanwhile
            else
            {
                guild = created;
                if (notice.Length > 0) await GuildService.Instance.SetNoticeAsync(guild, notice);
                // The creation tax, charged only once the guild exists; it is taken on a creation and
                // nothing on the refusal (see BuildAnsGuildCreate).
                if (fee > 0)
                {
                    await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);
                }
            }
        }

        Logger.LogInformation("Session {Id}: {Player} creates guild '{Guild}' -> id {GuildId} result {Result}, fee {Fee}, wallet {Wallet}",
            session.SessionId, player.Name, guildName, guild?.Id ?? 0, result, result == ResultSuccess ? fee : 0, player.Gold);

        var baseLevel = data.GuildExpTable is { Count: > 0 } exp
            ? exp.Min(r => r.Guild_Base_Level) : (byte)0;
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildCreate,
            (ReadOnlyMemory<byte>)BuildAnsGuildCreate(result, result == ResultSuccess ? guild : null, player.Name, baseLevel, player.Gold));

        if (result == ResultSuccess && guild != null)
        {
            // 2591 NfyPCViewInfoUpdate with the new guild line, sent together with the 2561 to the
            // creator and to the clients around them. Without it nobody around the creator sees the new guild
            // tag.
            var emblems = services.GetRequiredService<IGuildEmblemRepository>();
            await emblems.InitAsync();
            await PcViewInfoPackets.BroadcastToViewersAsync(player, guild, session, Broadcast, emblems);

            // No 2559 / 2560 here. Neither is pushed after a 2561: the client asks with 1424 right
            // after it and gets its 2591 + 2559 back (GuildInfoHandler), then asks for the roster
            // with 1425 from its 2559 handler (GuildMemListHandler). Pushing them here would come ahead of
            // those answers and double them.

            // The founder is now in a guild: an accepted "Achieved to join Guild" Khara (Complete_Type 43,
            // Khara 197) completes.
            await KharaService.OnGuildJoinedAsync(session, player, services, Logger, ct);
        }
    }

    /// <summary>The WStr16 notice after the name field: [u16 n @104][n UTF-16 chars @106]; empty when the
    /// request is too short to hold what its count claims.</summary>
    private static string ReadNotice(ReadOnlySpan<byte> body)
    {
        if (body.Length < NoticeOff + CountPrefixBytes) return string.Empty;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(NoticeOff, CountPrefixBytes));
        if (n == 0 || NoticeOff + CountPrefixBytes + n * WideCharBytes > body.Length) return string.Empty;
        return Encoding.Unicode.GetString(body.Slice(NoticeOff + CountPrefixBytes, n * WideCharBytes));
    }

    private const int GuildNameFieldBytes = 100;   // wchar[50], the same field 1426 sends
    private const int GuildNameMaxChars = 24;      // the clamp 1426 parsing, 2559 and 2591 all use
    private const int NameMaxChars = 21;           // the 2559 master-name clamp
    private const uint OleDateStatusNull = 2;      // COleDateTime "null", as the guildless 2559

    /// <summary>Initial capacity of the 2561 writer; a creation is 270 bytes.</summary>
    private const int AnsInitialCapacity = 288;

    /// <summary>Size of the VarBlob32 length prefix that opens the 2561 body.</summary>
    private const int VarBlob32LengthBytes = 4;

    /// <summary>Size of the guild GUID at the start of the identity record.</summary>
    private const int GuildGuidBytes = 16;

    /// <summary>Emblem index in the identity record: 0, a guild created this instant has none.</summary>
    private const ushort EmblemIndexNone = 0;

    /// <summary>Last byte of the 143-byte identity record: 1 on every 2561, refusal included (as 2591's +152).</summary>
    private const byte IdentityRecordTerminator = 1;

    /// <summary>First u16 of the roster summary: always 0, meaning unknown.</summary>
    private const ushort RosterSummaryLeadingField = 0;

    /// <summary>Count of an empty list (the u32 and u8 lists of the roster summary).</summary>
    private const ushort EmptyListCount = 0;

    /// <summary>Guild EXP of a guild created this instant.</summary>
    private const ulong GuildExpNone = 0;

    /// <summary>Flag byte between the guild progress blob and the notice: always 0.</summary>
    private const byte ProgressTrailerFlag = 0;

    /// <summary>Size of the OLE date record: {f64 date, u32 status, u32 high dword of the double}.</summary>
    private const int OleDateRecordBytes = 16;

    /// <summary>Offset of the u32 status inside the OLE date record.</summary>
    private const int OleDateStatusOffset = 8;

    /// <summary>Offset of the u32 high dword inside the OLE date record.</summary>
    private const int OleDateHighDwordOffset = 12;

    /// <summary>
    /// 2561 AnsGuildCreate (NONE): VarBlob32{u16 result, Blob&lt;143&gt;, u16, u16, WStr8, u16, Vec&lt;u32&gt;,
    /// u16, Vec&lt;u8&gt;, Blob&lt;25&gt;, u8, WStr16} followed by a u64 OUTSIDE the length prefix. A creation is 270 bytes, a refusal 218.
    /// - Blob&lt;143&gt; is the guild identity record in 2591's field order: [16 GUID][wchar[50] name]
    /// [u16 emblem index][24-byte robe {u32 id, u32 value, f64 date, u32 status, u32 hi}][u8 1]. The
    /// u8 is 1 (the refusal too), as 2591's +152. A GUID minted this instant has no emblem, and
    /// the robe is the Basic Robe in 2591's view form (<see cref="GuildRobeRecord"/>),
    /// the same one 2559/2591 send for the guild. The refusal zeroes the whole record.
    /// - then the 2559 object-3 roster summary: [u16 0][u16 1] on a creation and [0][0] on a
    /// refusal — the second is written as the member count (1 for a new guild), the first as the
    /// constant 0, its meaning unknown; the requester's name as WStr8 (the creator, who is the master,
    /// and on the refusal still the requester); the two lists empty, as in our 2559.
    /// - Blob&lt;25&gt; is the 2559 guild progress in another order: [u64 exp 0][u8 level 1][16-byte creation
    /// date {f64 OLE, u32 status 0, u32 hi}] on a creation; the refusal sends zeros with a null date.
    /// - u8 0, then the notice as WStr16 — the request's notice is echoed (no
    /// U+F801 terminator) and the refusal sends an empty one.
    /// - the trailing u64 is the requester's zeny AFTER the call. No 2435 is sent with a creation,
    /// so this u64 is how the client learns the wallet. The creation tax (string_ui 7883
    /// announces it) is the Config_Def type 2 value the handler charges, so <paramref name="wallet"/>
    /// is the wallet after that charge — and unchanged on a refusal.
    /// </summary>
    private static byte[] BuildAnsGuildCreate(ushort result, Guild? guild, string requesterName, byte guildBaseLevel, long wallet)
    {
        using var w = new PacketWriter(AnsInitialCapacity);
        var lenPos = w.Reserve(VarBlob32LengthBytes);
        var start = w.Length;
        w.WriteUInt16(result);

        // Identity record (Blob<143>): GUID, name field, emblem index, robe, terminator byte.
        if (guild != null)
        {
            var guid = new byte[GuildGuidBytes];
            if (guild.Guid16 != null) Array.Copy(guild.Guid16, guid, Math.Min(GuildGuidBytes, guild.Guid16.Length));
            w.WriteBytes(guid);
            w.WriteBytes(FixedWide(guild.Name, GuildNameMaxChars, GuildNameFieldBytes));
            w.WriteUInt16(EmblemIndexNone);            // no emblem yet
            w.WriteBytes(GuildRobeRecord.ViewForm());  // current robe: the Basic Robe
        }
        else
        {
            w.WriteZeros(GuildGuidBytes + GuildNameFieldBytes + sizeof(ushort) + GuildRobeRecord.ViewFormLength);
        }
        w.WriteByte(IdentityRecordTerminator);

        // Roster summary.
        int memberCount = 0;
        if (guild != null) lock (guild.Gate) memberCount = guild.Members.Count;
        w.WriteUInt16(RosterSummaryLeadingField);
        w.WriteUInt16((ushort)memberCount);
        var name = requesterName ?? string.Empty;
        if (name.Length > NameMaxChars) name = name.Substring(0, NameMaxChars);
        w.WriteByte((byte)name.Length);
        w.WriteBytes(Encoding.Unicode.GetBytes(name));
        w.WriteUInt16(EmptyListCount);                 // u32 list, empty
        w.WriteUInt16(EmptyListCount);                 // u8 list, empty

        // Guild progress: exp, level, creation date.
        w.WriteUInt64(GuildExpNone);                   // a guild created this instant has no EXP yet
        w.WriteByte(guild != null ? guildBaseLevel : (byte)0);
        var date = new byte[OleDateRecordBytes];
        WriteOleDate(date, guild?.CreatedUtc?.ToLocalTime());   // OLE dates are local time
        w.WriteBytes(date);

        w.WriteByte(ProgressTrailerFlag);
        var notice = guild?.Notice ?? string.Empty;
        w.WriteUInt16((ushort)notice.Length);
        w.WriteBytes(Encoding.Unicode.GetBytes(notice));
        w.WriteAt(lenPos, (uint)(w.Length - start));

        w.WriteUInt64((ulong)Math.Max(0, wallet));
        return w.ToArray();
    }

    private static byte[] FixedWide(string? s, int maxChars, int fieldBytes)
    {
        var field = new byte[fieldBytes];
        s ??= string.Empty;
        var n = Math.Min(Math.Min(s.Length, maxChars), fieldBytes / WideCharBytes - 1);
        for (int i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(i * WideCharBytes, WideCharBytes), s[i]);
        return field;
    }

    /// <summary>{f64 OLE date, u32 status, u32 = the double's high dword}; null is {0.0, 2, 0}.</summary>
    private static void WriteOleDate(Span<byte> dst, DateTime? when)
    {
        dst[..OleDateRecordBytes].Clear();
        if (when is null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(OleDateStatusOffset, sizeof(uint)), OleDateStatusNull);
            return;
        }
        var d = when.Value.ToOADate();
        BinaryPrimitives.WriteDoubleLittleEndian(dst, d);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(OleDateHighDwordOffset, sizeof(uint)), (uint)(BitConverter.DoubleToInt64Bits(d) >> 32));
    }
}
