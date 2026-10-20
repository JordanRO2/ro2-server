using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Job (profession) experience and level: the <see cref="RO2.Domain.Entities.Character.JobExp"/> and
/// <see cref="RO2.Domain.Entities.Character.JobLevel"/> pair, which is sent in the character snapshot and moved live
/// by <see cref="ProfessionExpPackets"/>.
/// </summary>
/// <remarks>
/// A gain is added to the experience, and the threshold of each level crossed is subtracted as the level rises.
/// Level 50 is the cap: any remainder is dropped there and nothing is sent at that level. Thresholds come from the
/// Base_Exp_Next column of the experience table, which equals Pro_Exp_Next on every row.
/// </remarks>
internal static class ProfessionExp
{
 /// <summary>The job level and in-level exp after a gain.</summary>
    internal readonly record struct Result(byte Level, ulong Exp, bool LevelledUp);

 /// <summary>
 /// Apply <paramref name="gain"/> to (<paramref name="level"/>, <paramref name="exp"/>): while the exp reaches
 /// the level's threshold, subtract it and go up one level. At the table's top level the exp is 0.
 /// </summary>
    internal static Result Apply(byte level, ulong exp, ulong gain, IReadOnlyDictionary<byte, ExpTableEntry> table)
    {
        byte cap = MaxLevel(table);
        byte start = level;
        ulong total = exp > ulong.MaxValue - gain ? ulong.MaxValue : exp + gain;
        while (level < cap && table.TryGetValue(level, out var row) && row.Base_Exp_Next > 0
               && total >= (ulong)row.Base_Exp_Next)
        {
            total -= (ulong)row.Base_Exp_Next;
            level++;
        }
        if (level >= cap) total = 0;
        return new Result(level, total, level > start);
    }

 /// <summary>The highest level of the exp table, 50 on the shipped data.</summary>
    internal static byte MaxLevel(IReadOnlyDictionary<byte, ExpTableEntry> table) =>
        table.Count == 0 ? (byte)0 : table.Keys.Max();

 /// <summary>
 /// The exp that takes (<paramref name="level"/>, <paramref name="exp"/>) exactly to <paramref name="target"/>
 /// with 0 left over: the thresholds of <paramref name="level"/> .. <paramref name="target"/> - 1, less the
 /// exp already held. 0 when the target is not above the level or a threshold row is missing.
 /// </summary>
    internal static ulong ExpToReach(byte level, ulong exp, byte target, IReadOnlyDictionary<byte, ExpTableEntry> table)
    {
        if (target <= level) return 0;
        ulong sum = 0;
        for (byte l = level; l < target; l++)
        {
            if (!table.TryGetValue(l, out var row) || row.Base_Exp_Next <= 0) return 0;
            sum += (ulong)row.Base_Exp_Next;
        }
        return sum > exp ? sum - exp : 0;
    }

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
        if (character.JobLevel >= MaxLevel(table) || !table.ContainsKey(character.JobLevel)) return false;

        var result = Apply(character.JobLevel, character.JobExp, gain, table);
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
    private static async Task PersistAsync(ICharacterRepository characters, uint characterId, Result result, ILogger logger)
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
