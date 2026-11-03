using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.Dungeons.Domain;

/// <summary>Pure entry checks for instance dungeons: result codes and the level / quest / entry-count rules.</summary>
public static class DungeonEntryRules
{
 /// Map type of an instance dungeon in Map_List.
    public const byte InstanceMapType = 1;

 /// Result code: the character's level is outside the dungeon range (string 10052).
    public const ushort ResultWrongLevel = 19001;

 /// Result code: the instance map could not be created (string 10055).
    public const ushort ResultInstanceMapFailed = 19004;

 /// Result code: a required quest has not been completed (string 10710).
    public const ushort ResultQuestRequired = 19012;

 /// Result code: the entry count for the dungeon is exhausted (string 15801).
    public const ushort ResultEnterCountExceeded = 19015;

 /// <summary>
 /// Returns the refusal code for an entry into the instance dungeon <paramref name="row"/>, or 0 to allow it.
 /// </summary>
 /// <param name="row">The dungeon row being entered.</param>
 /// <param name="level">The character's level.</param>
 /// <param name="questDone">True when the character has completed the row's required quest, or the row needs none.</param>
 /// <param name="wouldCount">True when the entry counts toward the entry limit.</param>
 /// <param name="used">The character's entry count for the row's dungeon in the current window.</param>
    public static ushort CheckInstanceEntry(DungeonInfo row, byte level, bool questDone, bool wouldCount, int used)
    {
        if ((row.Map_Min_Level != 0 && level < row.Map_Min_Level)
            || (row.Map_Max_Level != 0 && level > row.Map_Max_Level))
            return ResultWrongLevel;
        if (row.Need_Quest_ID != 0 && !questDone)
            return ResultQuestRequired;
        return CheckEnterCount(row, wouldCount, used);
    }

 /// <summary>
 /// Returns <see cref="ResultEnterCountExceeded"/> when an entry that counts finds the count at or above the
 /// row's limit (0 means no limit). Otherwise returns 0.
 /// </summary>
    public static ushort CheckEnterCount(DungeonInfo row, bool wouldCount, int used)
        => wouldCount && row.Enter_Count != 0 && used >= row.Enter_Count ? ResultEnterCountExceeded : (ushort)0;

 /// <summary>
 /// True when <paramref name="mapId"/> has an entry-control row. The Training Ground is the only such map. A
 /// 1521 into it is an instance entry for a party regardless of its map type.
 /// </summary>
    public static bool IsEntryControlled(GameDataStore data, uint mapId)
    {
        foreach (var control in data.EntryControl)
        {
            if (control.Map_ID == mapId)
                return true;
        }
        return false;
    }

 /// <summary>
 /// Returns <see cref="ResultInstanceMapFailed"/> when <paramref name="mapId"/> is entry-controlled and the
 /// requester is not in a party, otherwise 0.
 /// </summary>
    public static ushort CheckEntryControl(GameDataStore data, uint mapId, bool inParty)
        => !inParty && IsEntryControlled(data, mapId) ? ResultInstanceMapFailed : (ushort)0;
}
