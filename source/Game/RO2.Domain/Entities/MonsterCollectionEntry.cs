namespace RO2.Domain.Entities;

/// <summary>Summon level + DNA exp of one monster collection, keyed by the collection's NpcID.</summary>
public sealed class MonsterCollectionEntry
{
 /// <summary>CollectMonster.NpcID - the id the client sends in 1621/1622/1623.</summary>
    public uint NpcId { get; set; }

 /// <summary>CollectMonster.SummonLevel (1..4).</summary>
    public byte Level { get; set; } = 1;

 /// <summary>DNA uses banked toward the next SummonLevel; reset to 0 on level-up.</summary>
    public uint Exp { get; set; }
}
