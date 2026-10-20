namespace RO2.Domain.Entities;

/// <summary>
/// Pure slot rules of the pet incubators (place, put an egg in, collect) over a character's slot list.
/// The checks mirror the client's own: an incubator goes only onto an empty slot, and an egg only onto a
/// slot that holds a matching incubator with no egg. Lookups, hatch-time arithmetic and packet builders
/// live in the Zone host.
/// </summary>
public static class PetIncubatorRules
{
 /// <summary>Number of incubator slots per character.</summary>
    public const int SlotCount = 2;

    public static PetIncubatorSlot? SlotOf(IEnumerable<PetIncubatorSlot> slots, byte slot)
        => slots.FirstOrDefault(s => s.Slot == slot);

 /// <summary>
 /// Places an incubator (<paramref name="charges"/> is its Charge_Quantity) into the empty slot
 /// <paramref name="slot"/>, with no egg.
 /// </summary>
    public static bool TryPlace(List<PetIncubatorSlot> slots, byte slot, uint incubatorId, byte charges, DateTime nowUtc)
    {
        if (slot >= SlotCount || SlotOf(slots, slot) != null || charges == 0) return false;
        slots.Add(new PetIncubatorSlot
        {
            Slot = slot,
            IncubatorId = incubatorId,
            Charges = charges,
            EggId = 0,
            StartUtc = nowUtc,
            EndUtc = nowUtc,
        });
        return true;
    }

 /// <summary>
 /// Puts egg <paramref name="eggId"/> into <paramref name="slot"/>, which must hold the incubator
 /// <paramref name="incubatorId"/> with a charge left and no egg.
 /// </summary>
    public static bool TryStartIncubation(List<PetIncubatorSlot> slots, byte slot, uint incubatorId, uint eggId,
        DateTime startUtc, DateTime endUtc)
    {
        var s = SlotOf(slots, slot);
        if (s == null || s.IncubatorId != incubatorId || s.Charges == 0 || s.EggId != 0 || eggId == 0)
            return false;
        s.EggId = eggId;
        s.StartUtc = startUtc;
        s.EndUtc = endUtc;
        return true;
    }

 /// <summary>
 /// Returns the slot a collect may empty: it must hold the requested incubator and an egg that has hatched
 /// by <paramref name="nowUtc"/>. Returns null otherwise. Nothing is changed.
 /// </summary>
    public static PetIncubatorSlot? CollectableSlot(IEnumerable<PetIncubatorSlot> slots, byte slot, uint incubatorId, DateTime nowUtc)
    {
        var s = SlotOf(slots, slot);
        return s != null && s.IncubatorId == incubatorId && s.EggId != 0 && nowUtc >= s.EndUtc ? s : null;
    }

 /// <summary>Applies a collect once the hatched egg is granted: consumes one charge and empties or frees the slot.</summary>
    public static void ApplyCollect(List<PetIncubatorSlot> slots, PetIncubatorSlot s, DateTime nowUtc)
    {
        s.EggId = 0;
        s.Charges = (byte)Math.Max(0, s.Charges - 1);
        s.StartUtc = nowUtc;
        s.EndUtc = nowUtc;
        if (s.Charges == 0) slots.Remove(s);
    }

 /// <summary>Returns a copy of a slot, to restore if a later step fails.</summary>
    public static PetIncubatorSlot Copy(PetIncubatorSlot s) => new()
    {
        Slot = s.Slot, IncubatorId = s.IncubatorId, Charges = s.Charges, EggId = s.EggId,
        StartUtc = s.StartUtc, EndUtc = s.EndUtc,
    };

 /// <summary>Puts <paramref name="before"/> back in place of the slot's current entry.</summary>
    public static void RestoreSlot(List<PetIncubatorSlot> slots, PetIncubatorSlot before)
    {
        slots.RemoveAll(x => x.Slot == before.Slot);
        slots.Add(before);
    }
}
