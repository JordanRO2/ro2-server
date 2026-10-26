namespace RO2.Skills.Domain;

/// <summary>A learned skill of a character: a skill GROUP and the level it is learned at.</summary>
public class CharacterSkill
{
    /// <summary>The storage row id (0 for a not-yet-persisted entry).</summary>
    public ulong Id { get; set; }

    /// <summary>The owning character.</summary>
    public uint CharacterId { get; set; }

    /// <summary>The skill group id.</summary>
    public uint SkillId { get; set; }

    /// <summary>The learned level (1 by default).</summary>
    public byte Level { get; set; } = 1;
}
