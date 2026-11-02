using RO2.GameData;

namespace RO2.Quests.Domain;

/// <summary>
/// The login state-word computation of a held quest: what the 2310 shows for each objective row, and whether every
/// objective row is met. Pure: the stored document arrives decoded and the bag as a count lookup.
/// </summary>
public static class LoginProgressRule
{
    /// <summary>Per-objective <c>"met"</c> value and 2310 progress of a closed talk row.</summary>
    public const ushort TalkRowClosed = 1;

    /// <summary>
    /// Kill rows use their stored count. Collect and deliver rows use min(bag held, need). Talk rows are 1 when stored
    /// met. Use-item rows use their stored use count. Other row types are never met.
    /// </summary>
    /// <param name="kills">The stored kill counts (<see cref="QuestProgressDocument.Decode"/>), one per objective row.</param>
    /// <param name="met">The stored closes (<see cref="QuestProgressDocument.DecodeMet"/>), one per objective row.</param>
    /// <param name="bagCount">Items held by item id; null counts nothing held.</param>
    public static (ushort[] Values, bool AllMet) Compute(QuestData[] rows, ushort[] kills, ushort[] met,
        Func<uint, int>? bagCount)
    {
        var values = new ushort[ObjectiveRules.ObjectiveRowCount(rows)];
        var allMet = values.Length > 0;
        int objective = 0;
        for (int i = 0; i < rows.Length && objective < values.Length; i++)
        {
            var row = rows[i];
            if (row.Complete_Type == ObjectiveRules.NoObjective) continue;
            bool ok;
            switch (row.Complete_Type)
            {
                case ObjectiveRules.KillObjective:
                    values[objective] = kills[objective];
                    ok = ObjectiveRules.IsKillRow(row) && kills[objective] >= ObjectiveRules.NeededKills(row);
                    break;
                case ObjectiveRules.CollectObjective or ObjectiveRules.DeliverObjective:
                    var need = ObjectiveRules.NeededKills(row);
                    values[objective] = row.Complete_Type_Value1 != 0 && bagCount is not null
                        ? (ushort)Math.Min(bagCount(row.Complete_Type_Value1), need)
                        : (ushort)0;
                    ok = row.Complete_Type_Value1 != 0 && values[objective] >= need;
                    break;
                case ObjectiveRules.TalkObjective:
                    values[objective] = met[objective] != 0 ? TalkRowClosed : (ushort)0;
                    ok = values[objective] == TalkRowClosed;
                    break;
                case ObjectiveRules.UseItemObjective:
                    values[objective] = Math.Min(met[objective], ObjectiveRules.UsesNeeded(row));
                    ok = values[objective] >= ObjectiveRules.UsesNeeded(row);
                    break;
                default:
                    ok = false;
                    break;
            }
            allMet &= ok;
            objective++;
        }
        return (values, allMet);
    }
}
