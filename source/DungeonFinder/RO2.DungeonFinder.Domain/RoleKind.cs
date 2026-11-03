namespace RO2.DungeonFinder.Domain;

/// <summary>The finder role flags, as the client sends them.</summary>
public static class RoleKind
{
 /// <summary>Role flag for a tank.</summary>
    public const byte RoleTank = 1;

 /// <summary>Role flag for a damage dealer.</summary>
    public const byte RoleDealer = 2;

 /// <summary>Role flag for a healer.</summary>
    public const byte RoleHealer = 4;
}
