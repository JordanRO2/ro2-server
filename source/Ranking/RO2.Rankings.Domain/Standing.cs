namespace RO2.Rankings.Domain;

 /// <summary>A character's standing at the roll, before ranking.</summary>
    public readonly record struct Standing(uint CharId, long Score, DateTime ReachedUtc, string Name, byte ClassId,
        byte Profession, string Guild);

