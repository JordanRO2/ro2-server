namespace RO2.Worlds.Domain;

/// <summary>
/// The pure move-speed rule of a player: the speed it was last given is its mounted speed when one exists,
/// otherwise the standard world-entry speed. No state, no clock. The host resolves the ride and the mounted speed
/// (game-data and effect lookups) and hands the result in.
/// </summary>
public static class ActorMoveSpeedRules
{
    /// <summary>
    /// Movement speed sent at world entry when no class or mount speed source exists. Defaults to 6.0, the most common run speed.
    /// </summary>
    public const float PcModalWorldEntrySpeed = 6.0f;

    /// <summary>
    /// The speed a player is given: <paramref name="mountedSpeed"/> when it has one, otherwise
    /// <see cref="PcModalWorldEntrySpeed"/>. A null mounted speed covers both a player that is not riding and a
    /// vehicle with no usable speed row.
    /// </summary>
    public static float SpeedOf(float? mountedSpeed)
    {
        if (mountedSpeed is float mounted)
            return mounted;
        return PcModalWorldEntrySpeed;
    }
}
