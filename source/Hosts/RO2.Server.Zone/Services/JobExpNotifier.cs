using Microsoft.Extensions.Logging;
using RO2.Characters.Application;
using RO2.Characters.Domain;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Backs <see cref="IJobExpNotifier"/> for one grant: sends the 2526/2527 packets, logs a persist failure, and
/// re-checks the job-level Khara quests. Built per grant around the session and player (closing over broadcast,
/// logger and the scope), so the flow owns the order and the host owns the wire.
/// </summary>
internal sealed class JobExpNotifier(ProudSession session, PlayerState player, BroadcastService broadcast,
    ILogger logger, IServiceProvider services) : IJobExpNotifier
{
    public async Task AnnounceAsync(JobExpRules.Result result, ulong gain)
    {
        await session.SendRmiAsync(ZoneS2COpcodes.OnNotifyProJobExpChange,
            ProfessionExpPackets.BuildProJobExpChange2526(result.Exp, gain));
        if (result.LevelledUp)
        {
            var levelUp = ProfessionExpPackets.BuildProJobLevelUp2527(player.EntityId, result.Level);
            await session.SendRmiAsync(ZoneS2COpcodes.OnNotifyProJobLevelUpPC, levelUp);
            await broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnNotifyProJobLevelUpPC,
                w => w.WriteBytes(levelUp));
            logger.LogInformation("{Name} reached job level {Level}", player.Name, result.Level);
        }
    }

    public void PersistFailed(Exception ex, uint characterId, JobExpRules.Result result)
        => logger.LogWarning(ex, "Character {Id}: could not persist job level {Level} / exp {Exp}",
            characterId, result.Level, result.Exp);

    public Task OnLevelUpAsync() => KharaService.OnLevelChangedAsync(session, player, services, logger);
}
