using RO2.GameData.Tables;

namespace RO2.TacticsMarks.Domain;

/// <summary>
/// A party's ordered tactics-mark list. A numbered mark already on the list moves to the new target; the target
/// leaves any other entry. Thread-safe: every access takes the list's own lock.
/// </summary>
public sealed class TacticsMarkList
{
    private readonly List<TacticsMark> _marks = new();

    /// <summary>Records a mark on a target with the numbered flag and the time supplied.</summary>
    public void Set(uint targetHandle, ushort mark, bool numbered, DateTime setAtUtc)
    {
        lock (_marks)
        {
            int slot = numbered ? _marks.FindIndex(e => e.Mark == mark) : -1;
            if (slot >= 0)
            {
                _marks[slot] = new TacticsMark(mark, targetHandle, setAtUtc);
                for (int i = _marks.Count - 1; i >= 0; i--)
                    if (i != slot && _marks[i].Handle == targetHandle)
                        _marks.RemoveAt(i);
                return;
            }
            _marks.RemoveAll(e => e.Handle == targetHandle);
            if (_marks.Count < byte.MaxValue)
                _marks.Add(new TacticsMark(mark, targetHandle, setAtUtc));
        }
    }

    /// <summary>The marks in list order; with <paramref name="movedAtUtc"/>, only those set after that time.</summary>
    public TacticsMark[] Snapshot(DateTime? movedAtUtc = null)
    {
        TacticsMark[] entries;
        lock (_marks) entries = _marks.ToArray();
        return movedAtUtc is { } movedAt ? Array.FindAll(entries, e => e.SetAtUtc > movedAt) : entries;
    }

    /// <summary>Whether a mark is numbered, that is, its tactics target icon carries a number. False when there is no table.</summary>
    public static bool IsNumbered(IReadOnlyList<TacticsTarget>? rows, ushort mark)
    {
        if (rows is null || mark == 0) return false;
        foreach (var row in rows)
        {
            if (row.Tactics_List != mark) continue;
            var file = row.Route.Replace('\\', '/');
            file = file[(file.LastIndexOf('/') + 1)..];
            const string prefix = "Tactic_icon_", suffix = ".nif";
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
            var number = file[prefix.Length..^suffix.Length];
            return number.Length > 0 && number.All(char.IsAsciiDigit);
        }
        return false;
    }
}
