using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1690 ReqGuildHouseLotteryRank: the guild-house window asks for the lottery application ranking.
/// NONE envelope, empty body. Answered with 3021 AnsGuildHouseLotteryRank.
/// </summary>
/// <remarks>
/// <para>
/// REQUEST. Sent from the NPC dialog in the same millisecond as 1688 ReqGuildHouseResult. The 3021 answer
/// follows that pair's 3020 and is slow to arrive (seconds).
/// </para>
/// <para>
/// ANSWER, 3021, NONE envelope, no length prefix (read_side U16, CompactLen, Vec[Blob&lt;112&gt;],
/// CompactLen, Vec[Blob&lt;112&gt;]): <c>[u16 result][CompactLen n1][n1 x 112][CompactLen n2][n2 x 112]</c>.
/// Each 112-byte entry is <c>[u32 rank 1..10][wchar guildName[50], NUL-padded][u32 Lottery_Item Group_ID]
/// [u32 applied total]</c>.
/// <list type="bullet">
/// <item>List 1 is 40 entries: the 4 Lottery_Item groups x ranks 1-10, the total strictly falling with the
/// rank inside every group, in no sorted wire order (the first entry is rank 9 of group 2). Only 1 of
/// the 40 totals is a multiple of 100000, so a total is not a ticket count times the Config_Def 84
/// price; its unit is not established.</item>
/// <item>List 1 is a frozen snapshot of other players' guilds and must never be replayed.</item>
/// <item>List 2 is normally empty (<c>01 00</c>); when present it carries up to 4 entries after the 40 of
/// list 1.</item>
/// </list>
/// The client acts only on result 0: it opens window 215, fills list 2 (one row per group, keyed on the
/// Lottery_Item group: rank, name, total), then list 1 (rows grouped under the group's String_ID label,
/// each showing rank and name, and the total only on the row whose name equals its group's list-2 row).
/// So list 2 is the requesting guild's own standing, at most one row per group.
/// </para>
/// <para>
/// This server has no guild-house lottery, so no guild has applied: both lists go out empty, which the reader
/// accepts and which is the empty-list form (<c>01 00</c> closes list 2; the paired 3020 is
/// <c>0000 0100</c>). The previous body, <c>[u32 2][u16 0]</c>, had a u32 length prefix the client does
/// not read.
/// </para>
/// <para>
/// MISSING SYSTEM (the guild-house lottery, sized in GuildHouseTicketHandler): list 1 needs the applied total
/// of every guild per Lottery_Item group in the current round, top 10 per group, and list 2 the requester's
/// guild's own row per group. Both come from ticket purchases through 1686 (GuildHouseLotteryHandler),
/// which this server does not implement, and a round and draw lifecycle that is not defined.
/// </para>
/// </remarks>
public class GuildHouseLotteryRankHandler : ZoneRmiHandlerBase<GuildHouseLotteryRankHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildHouseLotteryRank;
    public override string Name => "GuildHouseLotteryRank";
    public GuildHouseLotteryRankHandler(IServiceScopeFactory s, ILogger<GuildHouseLotteryRankHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} requests guild house lottery rankings (no lottery state; empty lists)", player.Name);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildHouseLotteryRank,
            (ReadOnlyMemory<byte>)BuildAnsGuildHouseLotteryRank3021Empty(), ct);
    }

 /// <summary>
 /// 3021 with result 0 and both ranking lists empty: <c>00 00 | 01 00 | 01 00</c> (6 bytes).
 /// See the class remarks for the layout.
 /// </summary>
    internal static byte[] BuildAnsGuildHouseLotteryRank3021Empty()
    {
        using var w = new PacketWriter();
        w.WriteUInt16(0);          
        w.WriteCompactLength(0);   
        w.WriteCompactLength(0);   
        return w.ToArray();
    }
}
