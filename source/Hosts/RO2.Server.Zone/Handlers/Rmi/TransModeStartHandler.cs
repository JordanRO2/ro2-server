using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1413 ReqTransModeStart: starts the transformation mode for the requesting player.
/// </summary>
/// <remarks>
/// <para>
/// Request layout: <c>[01 08][u16 8][u16 58404][u32 handle]</c>. The body is not read.
/// </para>
/// <para>
/// Reply, in order: 2530 (result 0), 2529 (result 0) and 2531 to the transformer, then 2531 to nearby players.
/// A request made while a transform is already active is answered with 2530 carrying result 22652 only.
/// </para>
/// </remarks>
public class TransModeStartHandler : ZoneRmiHandlerBase<TransModeStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqTransModeStart;
    public override string Name => "TransModeStart";

    public TransModeStartHandler(IServiceScopeFactory s, ILogger<TransModeStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        if (!TransChargePackets.TryRecordTransModeStart(player.CharacterId, DateTime.UtcNow, out var chargeStart))
        {
            Logger.LogDebug("{Name} asks for a transform while one is running (ends {End:O}): refused", player.Name, chargeStart);
            var refused = TransModeNotifyPackets.BuildAckTransModeStartRefused2530(TransModeNotifyPackets.ResultAlreadyTransformed);
            await session.SendRmiAsync(ZoneS2COpcodes.OnAckTransModeStart, writer => writer.WriteBytes(refused), ct);
            return;
        }
        Logger.LogDebug("{Name} starts transformation mode", player.Name);

        
        
        
        var ackBody = ActorStatePackets.BuildOnAckTransModeStart();
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckTransModeStart, writer => writer.WriteBytes(ackBody), ct);

        
        
        
        var resetBody = TransChargePackets.BuildTransChargeGauge2529(0);
        await session.SendRmiAsync(ZoneS2COpcodes.OnNotifyTransChargeGague, writer => writer.WriteBytes(resetBody), ct);

        
        _ = EndTransformAsync(player.CharacterId, chargeStart);

        
        
        
        
        
        
        var start = await BuildTransModeStartAsync(player, services);
        if (start is not null)
        {
            await session.SendRmiAsync(ZoneS2COpcodes.OnNotifyTransModeStart, writer => writer.WriteBytes(start), ct);
            await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnNotifyTransModeStart, writer => writer.WriteBytes(start));
            
            
            
            await KharaService.OnGuardianSummonedAsync(session, player, services, Logger, ct);
        }
    }

 /// <summary>The 2531 body for <paramref name="player"/>, or null when it has no guardian grade to carry.</summary>
    private async Task<byte[]?> BuildTransModeStartAsync(PlayerState player, IServiceProvider services)
    {
        var character = player.Character;
        if (character is null)
            return null;
        byte profession = WorldEntryService.CreationProfessionByte(character);
        IReadOnlyList<uint> completed;
        try
        {
            completed = await services.GetRequiredService<IQuestRepository>().GetCompletedQuestIdsAsync(player.CharacterId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Name}: completed quests unavailable, no 2531", player.Name);
            return null;
        }
        var buff = TransModeNotifyPackets.ResolveTransBuff(services.GetRequiredService<GameDataStore>().ProJobType, profession, completed);
        if (buff is not { } b)
        {
            Logger.LogDebug("{Name}: no guardian grade (profession {Profession}, no completed guide quest of it): no 2531",
                player.Name, profession);
            return null;
        }
        return TransModeNotifyPackets.BuildNotifyTransModeStart2531(player.EntityId, b.SkillId, b.Grade);
    }

 /// <summary>
 /// Ends the transform after <c>TransChargePackets.TransModeTimeSeconds</c>. Sends 2532 OnNotifyTransModeEnd
 /// and the gauge reset 2529 (0) to the transformer, and 2532 to nearby players on the same map.
 /// The end is driven by a server timer, not by a client message.
 /// </summary>
 /// <remarks>
 /// The end is sent to everyone nearby at that moment, whether or not they received 2531. 2529 is sent only
 /// to the transformer. The timer resolves the character's current session when it fires, so a relog during
 /// the transform still receives the end. It does nothing if a newer 1413 has replaced this transform.
 /// </remarks>
    private async Task EndTransformAsync(uint characterId, DateTime chargeStartUtc)
    {
        try
        {
            var delay = chargeStartUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);

            if (!TransChargePackets.IsLatestTransform(characterId, chargeStartUtc))
                return;
            var player = PlayerManager.GetPlayerByCharacterId(characterId);
            if (player is null)
                return;
            var session = Broadcast.GetSession(player.SessionId);
            if (session is null)
                return;

            var endBody = TransModeNotifyPackets.BuildNotifyTransModeEnd2532(player.EntityId);
            await session.SendRmiAsync(ZoneS2COpcodes.OnNotifyTransModeEnd, writer => writer.WriteBytes(endBody));

            var body = TransChargePackets.BuildTransChargeGauge2529(0);
            await session.SendRmiAsync(ZoneS2COpcodes.OnNotifyTransChargeGague, writer => writer.WriteBytes(body));

            await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnNotifyTransModeEnd, writer => writer.WriteBytes(endBody));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Transform end (2532 + 2529) for character {Id} failed", characterId);
        }
    }
}
