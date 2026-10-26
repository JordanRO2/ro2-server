namespace RO2.Worlds.Application;

/// <summary>
/// The decisions inside the map-load acknowledgement (1304): the job-mastery notification, the glider-arrival view data, the
/// live level and the move-speed fallback. Each is pure: the handler keeps its whole orchestration and every send in its own
/// original order. The world-event trigger frames are decided by <see cref="WorldEventUseCase"/>; the handler only sends them.
/// </summary>
public static class MapLoadUseCase
{
    /// <summary>Where the self 2315 of a glider arrival comes from.</summary>
    public enum GliderViewDataSource
    {
        /// <summary>Built from the freshly loaded character row and its equipment.</summary>
        Character,

        /// <summary>The view data the glider flight prebuilt, used when the character row is unreadable.</summary>
        Prebuilt,

        /// <summary>None: the 2315 is not sent.</summary>
        None,
    }

    /// <summary>
    /// The total for the job-mastery point change, or null when there is no mastery (nothing is sent). A total beyond
    /// <see cref="uint.MaxValue"/> is clamped to it.
    /// </summary>
    public static uint? MasteryNotification(ulong masteryTotal)
        => masteryTotal > 0 ? (uint)Math.Min(masteryTotal, uint.MaxValue) : null;

    /// <summary>The source of the self 2315 on a glider arrival: the character row, else the prebuilt data, else none.</summary>
    public static GliderViewDataSource GliderViewData(bool characterLoaded, bool prebuiltPresent)
        => characterLoaded ? GliderViewDataSource.Character
            : prebuiltPresent ? GliderViewDataSource.Prebuilt
            : GliderViewDataSource.None;

    /// <summary>
    /// The level the character row is brought up to, or null to leave it: the live player level when the row exists, the
    /// player has a level and the row's differs.
    /// </summary>
    public static byte? LiveLevelOverride(byte? characterLevel, byte playerLevel)
        => characterLevel is { } level && playerLevel != 0 && level != playerLevel ? playerLevel : null;

    /// <summary>Whether the move speed (2497) still has to be sent: only when no earlier step of the reply sent it.</summary>
    public static bool NeedsMoveSpeedFallback(bool moveSpeedSent) => !moveSpeedSent;
}
