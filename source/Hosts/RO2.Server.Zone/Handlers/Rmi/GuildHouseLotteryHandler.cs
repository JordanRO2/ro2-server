using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1686 ReqGuildHouseLottery: the buy button of the guild-house ticket window (window 214) buys entry
/// tickets for one of the lotteries the 3019 just before it offered. NONE envelope, 8 bytes:
/// <c>[u32 Lottery_Item Group_ID][u32 ticket count]</c>.
/// </summary>
/// <remarks>
/// <para>
/// REQUEST. On the buy control the client sends the entry's id and the ticket count as an 8-byte pair.
/// The window checks neither guild, rank nor zeny, and it keeps no pending state: the click sends and
/// returns, so nothing in the client waits for the answer. The count arrows wrap within 1..255, so one
/// press below 1 gives 255. The id is one of the two that the preceding 3019 offered ({1, 3}).
/// </para>
/// <para>
/// ANSWER, 3018 AnsGuildHouseLottery: a bare <c>[u16 result]</c>, NONE envelope (read_side U16). The client
/// knows four codes and ignores any other:
/// <list type="bullet">
/// <item>0: string_ui 15344 "You have entered Guild House successfully.", then, if the guild window is
/// open, a guild refresh request.</item>
/// <item>24367: string_ui 8874 "Only Guild Master has the authority."</item>
/// <item>25207: string_ui 11738 "You have not registered guild." (next to 11739 "You have not registered
/// union.")</item>
/// <item>25208: string_ui 2086 "Not enough money to pay."</item>
/// </list>
/// A member who is not the master is answered with 24367, with no 2435 zeny
/// change following. 24367 is the master check: the master's rank is 0 (GuildService.MasterRankIndex).
/// </para>
/// <para>
/// What this server answers, from the requester's real guild state:
/// <list type="bullet">
/// <item>no guild: 25207, the no-guild message.</item>
/// <item>a member who is not the master: 24367.</item>
/// <item>the master: nothing. This server runs no guild-house lottery, so the id names no open lottery,
/// and no code fits: 0 would announce a purchase that did not happen, 25208 would blame funds, and a
/// master is never refused. Since the window keeps no pending state, no answer leaves nothing
/// hung.</item>
/// </list>
/// A stock client cannot reach this handler here anyway: our 3019 (GuildHouseTicketHandler) offers no
/// lottery, so window 214 has no entry to buy.
/// </para>
/// <para>
/// MISSING SYSTEM (the guild-house lottery; see GuildHouseTicketHandler). A master's purchase would have
/// to charge what the window shows, window count x unit price (every 3019 entry carries 100000 =
/// Config_Def type 84, "guild house application ticket minimum sale price"), answer
/// 25208 when the purse falls short, send the 2435, record the purchase per (round, Lottery_Item group,
/// guild) for the 3021 ranking, and only then answer 0. Which purse pays (wallet or guild funds) and how a
/// purchase adds to a 3021 total are not defined.
/// </para>
/// </remarks>
public class GuildHouseLotteryHandler : ZoneRmiHandlerBase<GuildHouseLotteryHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildHouseLottery;
    public override string Name => "GuildHouseLottery";
    public GuildHouseLotteryHandler(IServiceScopeFactory s, ILogger<GuildHouseLotteryHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>1686 body length: 8 bytes (read_side Blob&lt;8&gt;).</summary>
    internal const int BodyLength = 8;

    /// <summary>1686 body offset of the u32 Lottery_Item Group_ID.</summary>
    private const int LotteryIdOffset = 0;

    /// <summary>1686 body offset of the u32 ticket count (1..255 from the client's count arrows).</summary>
    private const int TicketCountOffset = 4;

    /// <summary>3018 body length: a single u16 result.</summary>
    private const int AnswerLength = 2;

 /// <summary>3018 code for a requester who is in a guild but not its master: string_ui 8874.</summary>
    internal const ushort ResultNotGuildMaster = 24367;

 /// <summary>3018 code for a requester with no guild: string_ui 11738.</summary>
    internal const ushort ResultNoGuild = 25207;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryReadRequest(payload.Span, out var lotteryId, out var ticketCount))
        {
            Logger.LogDebug("{Name} ReqGuildHouseLottery body is {Len} bytes, need {Need}", player.Name, payload.Length, BodyLength);
            return;
        }

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (RefusalFor(guild, player.CharacterId) is not { } result)
        {
            Logger.LogDebug("{Name} (master of guild {Guild}) asks for {Tickets} ticket(s) of guild house lottery {LotteryId}: " +
                            "no lottery is open on this server; no answer", player.Name, guild?.Name, ticketCount, lotteryId);
            return;
        }

        Logger.LogDebug("{Name} asks for {Tickets} ticket(s) of guild house lottery {LotteryId}: refused with {Result}",
            player.Name, ticketCount, lotteryId, result);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildHouseLottery,
            (ReadOnlyMemory<byte>)BuildAnsGuildHouseLottery3018(result), ct);
    }

 /// <summary>Reads the 1686 body <c>[u32 Lottery_Item Group_ID][u32 ticket count]</c>; false when it is
 /// shorter than <see cref="BodyLength"/>.</summary>
    internal static bool TryReadRequest(ReadOnlySpan<byte> body, out uint lotteryId, out uint ticketCount)
    {
        if (body.Length < BodyLength)
        {
            lotteryId = ticketCount = 0;
            return false;
        }
        lotteryId = BinaryPrimitives.ReadUInt32LittleEndian(body[LotteryIdOffset..]);
        ticketCount = BinaryPrimitives.ReadUInt32LittleEndian(body[TicketCountOffset..]);
        return true;
    }

 /// <summary>
 /// The 3018 refusal for <paramref name="characterId"/> given its guild: <see cref="ResultNoGuild"/> with
 /// no guild, <see cref="ResultNotGuildMaster"/> for a member who is not the master, and null for the
 /// master, who gets no answer while no lottery is open (see remarks).
 /// </summary>
    internal static ushort? RefusalFor(Guild? guild, uint characterId)
    {
        if (guild is null) return ResultNoGuild;
        return ReserveDeleteHandler.IsGuildMaster(guild, characterId) ? null : ResultNotGuildMaster;
    }

 /// <summary>3018 body: <c>[u16 result]</c>, 2 bytes, no envelope.</summary>
    internal static byte[] BuildAnsGuildHouseLottery3018(ushort result)
    {
        var body = new byte[AnswerLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }
}
