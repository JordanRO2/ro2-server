using Xunit;

namespace RO2.Tests;

/// <summary>
/// Locates the real game-data directory (the full, client-extracted table set) and reports whether it is present.
/// The public repo does NOT ship <c>data/</c> — those tables are client-derived (copyrighted); a contributor extracts
/// their own via <c>data-pipeline/</c> into <c>data/</c> (gitignored) or points <c>RO2_DATA_DIR</c> at it. Tests that
/// load the real tables use <see cref="DataFactAttribute"/>/<see cref="DataTheoryAttribute"/> so they SKIP (not fail)
/// when the data is absent, e.g. on CI or a fresh clone.
/// </summary>
internal static class DataDir
{
    /// <summary>The resolved real-data directory, or null when none is present.</summary>
    public static string? Path { get; } = Resolve();

    /// <summary>True when the full game-data tables are available to load.</summary>
    public static bool Present => Path is not null;

    private static string? Resolve()
    {
        var env = Environment.GetEnvironmentVariable("RO2_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env) && IsDataDir(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "data");
            if (IsDataDir(candidate)) return candidate;
        }
        return null;
    }

    // The same markers the per-file test resolvers key on: a real data/ carries both.
    private static bool IsDataDir(string d)
        => File.Exists(System.IO.Path.Combine(d, "Config_Def.json"))
        && File.Exists(System.IO.Path.Combine(d, "npc", "NpcRetailVitals.json"));
}

/// <summary>A <see cref="FactAttribute"/> that skips (does not fail) when the real game data is absent (see <see cref="DataDir"/>).</summary>
public sealed class DataFactAttribute : FactAttribute
{
    public DataFactAttribute()
    {
        if (!DataDir.Present) Skip = DataSkip.Reason;
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips when the real game data is absent (see <see cref="DataDir"/>).</summary>
public sealed class DataTheoryAttribute : TheoryAttribute
{
    public DataTheoryAttribute()
    {
        if (!DataDir.Present) Skip = DataSkip.Reason;
    }
}

internal static class DataSkip
{
    public const string Reason = "game data/ not present (set RO2_DATA_DIR or extract it with data-pipeline/); this data-dependent test is skipped";
}
