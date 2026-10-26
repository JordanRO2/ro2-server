using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;
using RO2.Characters.Application;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Job (profession) experience and level: the <see cref="RO2.Characters.Domain.Character.JobExp"/> and
/// <see cref="RO2.Characters.Domain.Character.JobLevel"/> pair, which is sent in the character snapshot and moved live
/// by <see cref="ProfessionExpPackets"/>.
/// </summary>
/// <remarks>
/// A gain is added to the experience, and the threshold of each level crossed is subtracted as the level rises.
/// Level 50 is the cap: any remainder is dropped there and nothing is sent at that level. Thresholds come from the
/// Base_Exp_Next column of the experience table, which equals Pro_Exp_Next on every row.
/// </remarks>
internal static class ProfessionExp
{




 /// <summary>
 /// Grant <paramref name="gain"/> job exp to <paramref name="player"/>'s character. Sends 2526 to the player,
 /// then, when a level was crossed, one 2527 to the player and the players around, and persists the new
 /// pair. Returns false, with nothing sent or changed, when there is no gain, no loaded character, or the
 /// character is at the cap or at a level with no table row.
 /// </summary>
 /// <remarks>
 /// A level-up then re-checks the job-level Khara quests through <paramref name="services"/>
 /// (<c>KharaService.OnLevelChangedAsync</c>), whose messages follow the 2527.
 /// </remarks>
    internal static Task<bool> GrantAsync(ProudSession session, PlayerState player, ulong gain,
        GameDataStore store, BroadcastService broadcast, ICharacterRepository characters, ILogger logger,
        IServiceProvider services)
    {
        var notifier = new JobExpNotifier(session, player, broadcast, logger, services);
        return JobExpGrantFlow.GrantAsync(player.Character, gain, new CharacterGameData(store), characters, notifier);
    }
}
