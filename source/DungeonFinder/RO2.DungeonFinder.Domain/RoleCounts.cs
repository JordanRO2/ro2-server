namespace RO2.DungeonFinder.Domain;

/// <summary>Filled slots per role.</summary>
public readonly record struct RoleCounts(byte Tanks, byte Healers, byte Dealers)
{
    public int Total => Tanks + Healers + Dealers;

 /// <summary>These counts plus one slot for <paramref name="role"/> (unchanged for 0 or an unknown role).</summary>
    public RoleCounts With(byte role) => role switch
    {
        RoleKind.RoleTank => this with { Tanks = (byte)(Tanks + 1) },
        RoleKind.RoleHealer => this with { Healers = (byte)(Healers + 1) },
        RoleKind.RoleDealer => this with { Dealers = (byte)(Dealers + 1) },
        _ => this,
    };

 /// <summary>The counts of <paramref name="seatRoles"/>.</summary>
    public static RoleCounts Of(IEnumerable<byte> seatRoles)
    {
        var c = default(RoleCounts);
        foreach (var r in seatRoles) c = c.With(r);
        return c;
    }
}
