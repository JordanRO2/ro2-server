using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1687 ReqGuildHouseTicket: the guild-house NPC's "entry ticket" menu asks which entry-ticket lotteries
/// are open. NONE envelope, empty body.
/// </summary>
/// <remarks>
/// <para>
/// REQUEST. Sent from the guild-house NPC dialog. It is always answered with a 3019.
/// </para>
/// <para>
/// ANSWER, 3019 AnsGuildHouseTicket, NONE envelope: <c>[u16 result][CompactLen n][n x {u32 Lottery_Item
/// Group_ID, u32 ticket price}]</c> (read_side U16, CompactLen, Vec[Blob&lt;8&gt;]). The client
/// opens buy window 214 and fills it only on result 0; any other result frees the list and shows nothing.
/// Valid forms:
/// <list type="bullet">
/// <item>Two entries, 20 bytes, e.g. {1, 100000} and {3, 100000}, or {2, 100000} and {4, 100000}. The ids are
/// Lottery_Item Group_IDs (1/2 Prontera, 3/4 Morroc: string_ui 15316/15317 "Prontera / Morroc House Entry Ticket") and
/// 100000 is Config_Def type 84, the guild-house application ticket's minimum sale price.</item>
/// <item>The empty form: result 0 and a zero-length list.</item>
/// </list>
/// Which pair is open, and when, is not defined by the data: there is no schedule.
/// </para>
/// <para>
/// This server sends the empty form (a closed lottery). Listing a lottery would put
/// a buy button in window 214 (which sends 1686) for a purchase this server
/// cannot carry out: GuildHouseLotteryHandler refuses every 1686, or leaves a master's unanswered.
/// </para>
/// <para>
/// MISSING SYSTEM, the guild-house lottery, one system with 1686-1690 and 3018-3024: (1) a schedule of open
/// Lottery_Item groups; (2) master-only ticket purchase at count x Config_Def 84 with the 3018 codes and a
/// 2435; (3) a per-(round, group, guild) ticket ledger feeding the 3021 ranking (GuildHouseLotteryRankHandler);
/// (4) a draw at the end of a round, whose rule is not defined; (5) the house award and its 3020 records
/// (GuildHouseResultHandler); (6) the 3024 announcement; (7) 1689/3022, moving a guild into its house
/// (GuildHouseTeleportHandler). Until it exists, the list stays empty.
/// </para>
/// </remarks>
public class GuildHouseTicketHandler : ZoneRmiHandlerBase<GuildHouseTicketHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildHouseTicket;
    public override string Name => "GuildHouseTicket";
    public GuildHouseTicketHandler(IServiceScopeFactory s, ILogger<GuildHouseTicketHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} requests guild house ticket info (no lottery is open; empty list)", player.Name);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildHouseTicket,
            (ReadOnlyMemory<byte>)BuildAnsGuildHouseTicket3019Empty(), ct);
    }

    /// <summary>AnsGuildHouseTicket result 0: success, so the client opens buy window 214.</summary>
    private const ushort ResultOk = 0;

    /// <summary>Number of open lottery entries in the answer: none.</summary>
    private const int NoOpenLotteries = 0;

    /// <summary>
    /// 3019 with result 0 and no open lottery (4 bytes), the empty answer.
    /// See the class remarks.
    /// </summary>
    internal static byte[] BuildAnsGuildHouseTicket3019Empty()
    {
        using var w = new PacketWriter();
        w.WriteUInt16(ResultOk);
        w.WriteCompactLength(NoOpenLotteries);
        return w.ToArray();
    }
}
