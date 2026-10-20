using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1614 ReqHelperDeclare: a party member declares itself a Helper, or quits being one.</summary>
/// <remarks>
/// <para>
/// The request body is <c>[u64 own character GID][u8 flag]</c>, 9 bytes. Flag 1 declares a Helper; flag 0 quits.
/// </para>
/// <para>
/// <see cref="PartyService.SetHelperFlagAsync"/> stores the flag in the party roster, answers 2902 to the declarer, and sends the updated roster (2361) to the party. A flag set by a player is not announced with 2903; that notice is used when the server revokes a lapsed Helper (see <see cref="PartyService.RevokeLapsedHelpersAsync"/>).
/// </para>
/// <para>
/// The request is dropped when the GID is not the sender's own, the flag is neither 0 nor 1, the sender is in no party, or the sender declares a Helper who fails the level rule (<see cref="HelperPackets.CanBeHelper"/>).
/// </para>
/// </remarks>
public class HelperDeclareHandler : ZoneRmiHandlerBase<HelperDeclareHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqHelperDeclare;
    public override string Name => "HelperDeclare";

    public HelperDeclareHandler(IServiceScopeFactory s, ILogger<HelperDeclareHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < HelperPackets.DeclareBodyLen)
        {
            Logger.LogDebug("{Name}: 1614 of {Len} bytes, expected {Expected}; dropped",
                player.Name, payload.Length, HelperPackets.DeclareBodyLen);
            return;
        }
        var gid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);
        var flag = payload.Span[8];

        if (flag is not (HelperPackets.FlagDeclared or HelperPackets.FlagQuit)
            || gid != PartyAnswerPackets.MemberGid(player))
        {
            Logger.LogInformation("{Name}: 1614 for GID 0x{Gid:X16} flag {Flag} is not its own declaration; dropped",
                player.Name, gid, flag);
            return;
        }

        switch (await services.GetRequiredService<PartyService>().SetHelperFlagAsync(player, flag))
        {
            case HelperDeclareResult.NotInParty:
                Logger.LogInformation("{Name}: 1614 flag {Flag}, but this character is in no party on this session; dropped",
                    player.Name, flag);
                break;
            case HelperDeclareResult.NotEligible:
                Logger.LogInformation("{Name} (level {Level}): 1614 declares a Helper, but no other party member is "
                    + "{Gap} or more levels below; dropped", player.Name, player.Level, HelperPackets.LevelGap);
                break;
        }
    }
}
