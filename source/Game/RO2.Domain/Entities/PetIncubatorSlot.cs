namespace RO2.Domain.Entities;

/// <summary>One of the two incubator slots of the pet-collection window.</summary>
public sealed class PetIncubatorSlot
{
 /// <summary>Slot index, 0 or 1.</summary>
    public byte Slot { get; set; }

 /// <summary>Incubator_ID of the incubator placed in this slot.</summary>
    public uint IncubatorId { get; set; }

 /// <summary>Charges remaining: the placed quantity, decremented after each collect.</summary>
    public byte Charges { get; set; }

 /// <summary>Pet_Egg_ID of the incubating egg, or 0 when the slot holds none.</summary>
    public uint EggId { get; set; }

 /// <summary>When the egg was placed, or when the incubator was placed or last emptied for an empty slot.</summary>
    public DateTime StartUtc { get; set; }

 /// <summary>When the egg hatches; equal to <see cref="StartUtc"/> for an empty slot.</summary>
    public DateTime EndUtc { get; set; }
}
