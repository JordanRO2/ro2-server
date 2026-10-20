using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// SkillStartHandler's cooldown refusal asks <see cref="RefusalCode"/>, and its accepted path calls
/// <see cref="RecordAccepted"/>.
/// </summary>
/// <remarks>
/// Not modelled, named so nobody reads this as complete: 22900, 22303 and 20119 have no discriminator
/// recovered and are not sent by this server. DATA: Casting_Time + Excution_Time of the skill id's
/// Skill_Table_SkillLevel row.
/// </remarks>
internal static class SkillCasterBusyWindow
{
 /// <summary>
 /// 2715 / 2962 result for a cast refused on cooldown while the caster is idle: string_ui 2549, "Do
 /// not have enough time to cast this skill to use.". Kept here, not in SkillPackets.
 /// </summary>
    internal const ushort SkillResultNotEnoughTime = 20032;

    private sealed class LastCast
    {
        internal readonly Lock Gate = new();
        internal DateTime StartedAt;
        internal TimeSpan Duration;
    }

    private sealed record CastTimeRow(uint ID, float Casting_Time, float Excution_Time);

    private static readonly ConditionalWeakTable<PlayerState, LastCast> Last = new();

    private static readonly Lazy<FrozenDictionary<uint, TimeSpan>> Shipped =
        new(() => LoadDurations(FindShippedTable()));

 /// <summary>
 /// data/Skill_Table_SkillLevel.json relative to the working directory, where the servers run
 /// (GameDataStore loads "data" the same way); failing that, the first data/ found walking up from
 /// the executable's directory, so a process started elsewhere reads the same table instead of treating
 /// every cast as zero-length.
 /// </summary>
    private static string FindShippedTable()
    {
        var relative = Path.Combine("data", "Skill_Table_SkillLevel.json");
        if (File.Exists(relative)) return relative;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return relative;
    }

 /// <summary>
 /// Casting_Time + Excution_Time of <paramref name="skillId"/> (zero when the table has no row).
 /// </summary>
    internal static TimeSpan CastDuration(uint skillId) =>
        Shipped.Value.TryGetValue(skillId, out var d) ? d : TimeSpan.Zero;

 /// <summary>
 /// Records an accepted 1508 of <paramref name="skillId"/> at <paramref name="nowUtc"/>.
 /// </summary>
    internal static void RecordAccepted(PlayerState player, uint skillId, DateTime nowUtc) =>
        Record(player, nowUtc, CastDuration(skillId));

 /// <summary>
 /// Stores the caster's last accepted cast: its start and its Casting_Time + Excution_Time.
 /// </summary>
    internal static void Record(PlayerState player, DateTime startedAtUtc, TimeSpan duration)
    {
        var last = Last.GetValue(player, static _ => new LastCast());
        lock (last.Gate)
        {
            last.StartedAt = startedAtUtc;
            last.Duration = duration;
        }
    }

 /// <summary>
 /// True when the caster's last accepted cast began more than its duration before <paramref
 /// name="nowUtc"/>, or when there is none.
 /// </summary>
    internal static bool IsIdle(PlayerState player, DateTime nowUtc)
    {
        if (!Last.TryGetValue(player, out var last)) return true;
        lock (last.Gate)
        {
            return nowUtc - last.StartedAt > last.Duration;
        }
    }

 /// <summary>
 /// The code of a cooldown refusal: <see cref="SkillResultNotEnoughTime"/> when idle, else <see
 /// cref="SkillPackets.SkillResultOnCooldown"/>.
 /// </summary>
    internal static ushort RefusalCode(PlayerState player, DateTime nowUtc) =>
        IsIdle(player, nowUtc) ? SkillResultNotEnoughTime : SkillPackets.SkillResultOnCooldown;

 /// <summary>
 /// Casting_Time + Excution_Time per skill id from a Skill_Table_SkillLevel.json; empty when the
 /// file is missing or unreadable (every cast then counts as idle once its start has passed).
 /// </summary>
    internal static FrozenDictionary<uint, TimeSpan> LoadDurations(string path)
    {
        try
        {
            if (!File.Exists(path)) return FrozenDictionary<uint, TimeSpan>.Empty;
            var rows = JsonSerializer.Deserialize<List<CastTimeRow>>(File.ReadAllBytes(path));
            if (rows is null) return FrozenDictionary<uint, TimeSpan>.Empty;
            var map = new Dictionary<uint, TimeSpan>(rows.Count);
            foreach (var r in rows)
                map[r.ID] = TimeSpan.FromSeconds((double)r.Casting_Time + r.Excution_Time);
            return map.ToFrozenDictionary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return FrozenDictionary<uint, TimeSpan>.Empty;
        }
    }
}
