namespace RO2.Characters.Domain;

/// <summary>Pure rules of the review-another-player reply (RMI 1696 → 3033): the vitals clamp and the worn-gear filter.</summary>
public static class ReviewRules
{
    /// <summary>Clamps the reviewed player's current HP/SP to the sheet's maxima for the 3033 stat block.</summary>
    public static (uint hp, ushort sp) ClampVitals(long hp, int mp, uint maxHp, ushort maxSp)
        => ((uint)Math.Clamp(hp, 0, maxHp), (ushort)Math.Clamp(mp, 0, maxSp));

    /// <summary>True for an item the 3033 reply shows as worn gear: the equipped storage (StorageType 1).</summary>
    public static bool IsWornForDisplay(byte storageType) => storageType == 1;
}
