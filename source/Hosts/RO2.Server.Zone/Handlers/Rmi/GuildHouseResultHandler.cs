using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1688 ReqGuildHouseResult - the guild-house window asks for the lottery results. Empty body.
/// </summary>
/// <remarks>
/// <para>
/// TRIGGER. The NPC dialog sends 1690 and 1688 back to back, so both requests leave in the same
/// millisecond; the 3020 answer comes back first, before the 3021. On result 0 the 3020 consumer toggles
/// window 215 and fills its result list.
/// </para>
/// <para>
/// 3020 AnsGuildHouseResult is [u16 result][CompactLen n][n x 152-byte result record]. The usual answer is
/// the 4 bytes 00 00 01 00: result 0 and an empty list - what this handler sends.
/// The old reply, u16 0 + u32 0, put a 0x00 where the CompactLen width byte goes and two stray bytes after it.
/// </para>
/// <para>
/// A non-empty answer is 156 bytes with one 152-byte record, which the client reads as: +0 a 16-byte record
/// GUID; +16 u32 the house's Map_Index, looked up in the map table and shown by its Map_List String_ID (for
/// example 5203, "Prontera Guild House 3", Map_Type 9, String_ID 15320); +20 wchar[50] the winning guild
/// (for example "NO_WINNER"); +120 and +136 two 16-byte dates shown as a start~end period (an OLE date
/// double at +0). Such a record is a draw outcome: house, winner, tenure. This
/// server has no guild-house draw or house ownership (the system GuildHouseTicketHandler / 3019 would need), so a
/// record, the NO_WINNER form included, cannot be filled from real state and the list stays empty.
/// </para>
/// </remarks>
public class GuildHouseResultHandler : ZoneRmiHandlerBase<GuildHouseResultHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildHouseResult;
    public override string Name => "GuildHouseResult";
    public GuildHouseResultHandler(IServiceScopeFactory s, ILogger<GuildHouseResultHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} requests guild house result", player.Name);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildHouseResult, (ReadOnlyMemory<byte>)BuildEmptyResult());
    }

    /// <summary>3020 result code 0: success, so the client toggles the results window and fills its list.</summary>
    private const ushort ResultOk = 0;

    /// <summary>Result-record count of an empty list; encoded as the compact length <c>01 00</c>.</summary>
    private const int NoResultRecords = 0;

    /// <summary>3020 with result 0 and no result record: <c>00 00 01 00</c> ([u16 0][CompactLen 0]).</summary>
    internal static byte[] BuildEmptyResult()
    {
        using var w = new PacketWriter(4);
        w.WriteUInt16(ResultOk);
        w.WriteCompactLength(NoResultRecords);
        return w.ToArray();
    }
}
