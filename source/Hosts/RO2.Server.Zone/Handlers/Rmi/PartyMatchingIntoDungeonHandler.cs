using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1569 ReqPartyMatching_Into_Dungeon_Participation: enter the matched dungeon. Answered with
/// 2825 NfyDungeonFind_Party_Cancel, which carries no payload at all.
/// </summary>
/// <remarks>
/// <para>
/// The 2825 body is EMPTY: its dispatch case reads no fields, the message carries no payload argument, and the
/// handler takes no arguments. It turns window 41's finding state off, hides window 39 and hides window 46. The old
/// body [01] was a byte the client never reads.
/// </para>
/// <para>
/// What is missing. A real 2825 is the server cancelling a party's dungeon-finder search, and the server keeps
/// no party search queue (1555 / 1558 are answered statelessly), so nothing else can trigger it. This handler
/// is its only sender, and 1569 itself is not sent by the build 303 client. Answering it with the finder-UI reset is
/// the neutral answer.
/// </para>
/// </remarks>
public class PartyMatchingIntoDungeonHandler : ZoneRmiHandlerBase<PartyMatchingIntoDungeonHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatching_Into_Dungeon_Participation;
    public override string Name => "PartyMatching_Into_Dungeon_Participation";

    public PartyMatchingIntoDungeonHandler(IServiceScopeFactory s, ILogger<PartyMatchingIntoDungeonHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogInformation("{Name}: 1569 enter matched dungeon ({Len}B); no dungeon-finder queue exists, answered with the 2825 finder reset",
            player.Name, payload.Length);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyDungeonFind_Party_Cancel, BuildNfyDungeonFindPartyCancel2825());
    }

 /// 2825 NfyDungeonFind_Party_Cancel: no payload.
    internal static byte[] BuildNfyDungeonFindPartyCancel2825() => [];
}
