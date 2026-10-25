using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1612 ReqCutSceneStart, sent by the client when it starts a cutscene.</summary>
/// <remarks>
/// The request body is a u32 scene index that refers to a Scene_info row. The reply, AnsCutSceneStart (2900),
/// echoes the index followed by a u16 result of 0. For party scenes, the other online members of the requester's
/// party are also sent NfyCutSceneStart (2904).
/// </remarks>
public class CutSceneStartHandler : ZoneRmiHandlerBase<CutSceneStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCutSceneStart;
    public override string Name => "CutSceneStart";

    public CutSceneStartHandler(IServiceScopeFactory s, ILogger<CutSceneStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4) return;
        var sceneIndex = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsCutSceneStart, writer =>
        {
            writer.WriteUInt32(sceneIndex); 
            writer.WriteUInt16(0);          
        });

        await NotifyPartyAsync(session, player, sceneIndex, services);
    }

 /// <summary>
 /// Sends NfyCutSceneStart (2904) to the other online party members on the same map when the scene is a party scene.
 /// </summary>
    private async Task NotifyPartyAsync(ProudSession session, PlayerState player, uint sceneIndex, IServiceProvider services)
    {
        var scene = services.GetRequiredService<GameDataStore>().SceneInfo.FirstOrDefault(s => s.Index == sceneIndex);
        if (scene is null || scene.Party != 1) return;

        var party = services.GetRequiredService<PartyService>().GetPlayerParty(session.SessionId);
        if (party is null) return;

        var sent = 0;
        
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            if (memberId == session.SessionId) continue;
            var member = PlayerManager.GetPlayer(memberId);
            if (member is null || member.MapId != player.MapId) continue;
            var memberSession = Broadcast.GetSession(memberId);
            if (memberSession is null) continue;
            await memberSession.SendRmiAsync(ZoneS2COpcodes.NfyCutSceneStart, w => w.WriteUInt32(sceneIndex));
            sent++;
        }

        Logger.LogDebug("{Name} started party cutscene {Scene} on map {Map}: 2904 to {Sent} member(s)",
            player.Name, sceneIndex, player.MapId, sent);
    }
}
