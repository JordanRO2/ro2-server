namespace RO2.Collections.Domain;

/// <summary>State of one monster collection: NPC id, the skill of its current summon level, the level, and the experience toward the next level.</summary>
public readonly record struct MonsterCollectionState(uint NpcId, uint SkillId, byte Level, uint Exp);

/// <summary>State of one pet collection: pet id, the loyalty effect of the reached loyalty level, that level, and the loyalty value.</summary>
public readonly record struct PetCollectionState(uint PetId, uint SkillId, byte LoyaltyLevel, ushort Loyalty);

/// <summary>Bag counts of a collection's four DNA pieces.</summary>
public readonly record struct PieceCounts(ushort Piece1, ushort Piece2, ushort Piece3, ushort Piece4);

/// <summary>Collect-rate bucket carried by the monster collection header.</summary>
public readonly record struct CollectRateHeader(byte Index, byte SkillMax, uint CooltimeMs);
