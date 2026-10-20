namespace RO2.Domain.Entities;

/// <summary>
/// Character learned skill domain entity
/// </summary>
public class CharacterSkill
{
    public ulong Id { get; set; }
    public uint CharacterId { get; set; }
    public uint SkillId { get; set; }
    public byte Level { get; set; } = 1;

    
    public virtual Character Character { get; set; } = null!;
}
