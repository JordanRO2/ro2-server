using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1467 ReqGuildDelAuthority — the master deletes one title from the guild's rank table.
/// </summary>
/// <remarks>
/// <para>
/// Request, 17 bytes, no envelope:
/// <c>[16 guild GUID][u8 title index]</c>. It is sent
/// by the rank editor's save: for each of the ten rows whose title the
/// client holds but whose edit box is now empty, it sends
/// the character's guild GUID and the row index. The editor sends nothing unless the
/// requester is the master (the requester's rank byte must be 0, else string_ui 8874) and at least two rows stay filled
/// in (string_ui 8873).
/// </para>
/// <para>
/// Answer 2632 AnsGuildDelAuthority <c>[u16 result]</c>, NONE. Its consumer
/// shows string_ui 9039 "Title options have been modified." on 0, opens string_ui 9038 "You can't delete
/// the title since a guild member still has this title." on 18, and passes any other code to
/// the guild result handler; every non-zero code also refreshes the member list. The codes
/// used here are the ones that function maps to a message about exactly this request: 7 "Cannot Find
/// Guild", 17 "Unauthorized Guild right", 21 "Wrong number for the right", 22 "Minimum number of the
/// right", 25 "Number of the right that cannot be deleted", 37 "No right is existing" (string_etc
/// 2000000068 / 73 / 76 / 77 / 80 / 90).
/// </para>
/// <para>
/// On success the title is removed and stored, 2632 0 goes to the requester and 2636 NfyGuildDelAuthority
/// <c>[u8 index]</c> to every online member, the requester included — the way 1468's 2637 is sent
/// (GuildModifyAuthorityHandler). 2636's consumer blanks the client's 24-byte title
/// record [index] (index, empty name, zero permissions) and refreshes the member list, so
/// without it the editor and every member keep showing the deleted title until the next 2628. A 2636
/// reaches members that sent no 1467, so it is broadcast like 2637.
/// </para>
/// </remarks>
public class GuildDelAuthorityHandler : ZoneRmiHandlerBase<GuildDelAuthorityHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildDelAuthority;
    public override string Name => "GuildDelAuthority";
    public GuildDelAuthorityHandler(IServiceScopeFactory s, ILogger<GuildDelAuthorityHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>[16 GUID][u8 index]: guild GUID, then the title row index.</summary>
    internal const int RequestLength = 17;
    private const int GuidLength = 16;

    internal const ushort ResultOk = 0;
    /// <summary>"Cannot Find Guild": no guild, a disbanded one, or not the requester's GUID.</summary>
    internal const ushort ResultGuildNotFound = 7;
    /// <summary>"Unauthorized Guild right": the requester is not the master.</summary>
    internal const ushort ResultNotMaster = 17;
    /// <summary>2632 code 18, the consumer's own dialog (string_ui 9038): a member still holds the title.</summary>
    internal const ushort ResultTitleInUse = 18;
    /// <summary>"Wrong number for the right": an index outside 0..9.</summary>
    internal const ushort ResultOutOfRange = 21;
    /// <summary>"Minimum number of the right": the table would keep fewer than two titles.</summary>
    internal const ushort ResultTooFewTitles = 22;
    /// <summary>"Number of the right that cannot be deleted": title 0, the master's (string_ui 9052 "Guild
    /// Master's title cannot be deleted, it only can be modified.").</summary>
    internal const ushort ResultMasterTitle = 25;
    /// <summary>"No right is existing": the table holds no title with that index.</summary>
    internal const ushort ResultNoSuchTitle = 37;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength)
        {
            Logger.LogWarning("{Name} sent a {Len}-byte ReqGuildDelAuthority, need {Need}", player.Name, payload.Length, RequestLength);
            return;
        }
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        var index = payload.Span[GuidLength];
        var guidMatches = guild != null && GuidMatches(guild, payload.Span[..GuidLength]);

        var result = guild == null || !guidMatches
            ? ResultGuildNotFound
            : ResultOf(await GuildService.Instance.DeleteRankAsync(guild, player.CharacterId, index));

        Logger.LogInformation("Session {Id}: {Name} deletes guild title {Index} in guild {Guild} -> result {Result}",
            session.SessionId, player.Name, index, guild?.Id ?? 0, result);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildDelAuthority, (ReadOnlyMemory<byte>)BuildAnsGuildDelAuthority2632(result));

        if (result == ResultOk && guild != null)
            await GuildPackets.BroadcastToGuildAsync(guild, PlayerManager, Broadcast, ZoneS2COpcodes.NfyGuildDelAuthority,
                BuildNfyGuildDelAuthority2636(index));
    }

    private static bool GuidMatches(Guild g, ReadOnlySpan<byte> guid)
    {
        lock (g.Gate) return g.Guid16 != null && guid.SequenceEqual(g.Guid16);
    }

    /// <summary>The 2632 code for a <see cref="GuildService.DeleteRankAsync"/> outcome.</summary>
    internal static ushort ResultOf(GuildService.DeleteRankRefusal refusal) => refusal switch
    {
        GuildService.DeleteRankRefusal.None => ResultOk,
        GuildService.DeleteRankRefusal.NotMaster => ResultNotMaster,
        GuildService.DeleteRankRefusal.MasterTitle => ResultMasterTitle,
        GuildService.DeleteRankRefusal.OutOfRange => ResultOutOfRange,
        GuildService.DeleteRankRefusal.NoSuchTitle => ResultNoSuchTitle,
        GuildService.DeleteRankRefusal.TitleInUse => ResultTitleInUse,
        GuildService.DeleteRankRefusal.TooFewTitles => ResultTooFewTitles,
        _ => ResultGuildNotFound,   // NotRegistered: disbanded after the lookup
    };

    /// <summary>2632 AnsGuildDelAuthority: [u16 result].</summary>
    internal static byte[] BuildAnsGuildDelAuthority2632(ushort result) => [(byte)result, (byte)(result >> 8)];

    /// <summary>2636 NfyGuildDelAuthority: [u8 title index] (the client passes the byte to
    /// the member-list refresh).</summary>
    internal static byte[] BuildNfyGuildDelAuthority2636(byte index) => [index];
}
