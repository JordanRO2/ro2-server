using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;

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
    internal static async Task<bool> GrantAsync(ProudSession session, PlayerState player, ulong gain,
        GameDataStore store, BroadcastService broadcast, ICharacterRepository characters, ILogger logger,
        IServiceProvider services)
    {
        if (gain == 0 || player.Character is not { } character) return false;
        var table = store.ExpTable;
        if (character.JobLevel >= JobExpRules.MaxLevel(table) || !table.ContainsKey(character.JobLevel)) return false;

        var result = JobExpRules.Apply(character.JobLevel, character.JobExp, gain, table);
        character.JobLevel = result.Level;
        character.JobExp = result.Exp;

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

        await PersistAsync(characters, character.Id, result, logger);
        if (result.LevelledUp)
            await KharaService.OnLevelChangedAsync(session, player, services, logger);
        return true;
    }

 /// <summary>
 /// Writes the new level and experience to the character's stored row. The row is read fresh, and only JobLevel and
 /// JobExp are changed before it is saved, so other fields updated elsewhere are not overwritten. This pair is not
 /// carried by <c>BulkSavePlayerStateAsync</c>, so this write is required.
 /// </summary>
    private static async Task PersistAsync(ICharacterRepository characters, uint characterId, JobExpRules.Result result, ILogger logger)
    {
        try
        {
            var row = await characters.GetByIdAsync(characterId);
            if (row is null) return;
            row.JobLevel = result.Level;
            row.JobExp = result.Exp;
            await characters.SaveCharacterAsync(row);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Character {Id}: could not persist job level {Level} / exp {Exp}",
                characterId, result.Level, result.Exp);
        }
    }
}
