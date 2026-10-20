using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Persistence of a character's pet-incubator slots (1630 place an incubator, 1631 put an egg in, 1632 collect the
/// hatched egg; answered by 2926), one stored list per character. The slot rules are
/// <see cref="PetIncubatorRules"/>; the GameData lookups, the hatch-time arithmetic, the per-character request lock
/// and the packet builders stay in the Zone host. Every method reads, changes and stores the list in one call
/// WITHOUT its own serialisation: the caller holds the per-character lock around the whole request, as before.
/// Each method throws when the storage cannot be read or written; the Zone host logs and treats that as a refusal.
/// </summary>
public interface IPetIncubatorRepository
{
 /// <summary>The character's slots (empty when it has none).</summary>
    Task<IReadOnlyList<PetIncubatorSlot>> GetSlotsAsync(uint characterId, CancellationToken ct = default);

 /// <summary>
 /// 1630: stores a fresh incubator (<paramref name="charges"/> = its Charge_Quantity) in the empty slot
 /// <paramref name="slot"/>. False, nothing stored, for a slot index of 2 or more, an occupied slot or 0 charges.
 /// </summary>
    Task<bool> TryPlaceIncubatorAsync(uint characterId, byte slot, uint incubatorId, byte charges, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>
 /// 1631: starts egg <paramref name="eggId"/> in the slot, which must hold <paramref name="incubatorId"/> with a
 /// charge left and no egg. False, nothing stored, otherwise. The charges do not move while an egg is in.
 /// </summary>
    Task<bool> TryStartIncubationAsync(uint characterId, byte slot, uint incubatorId, uint eggId, DateTime startUtc, DateTime endUtc, CancellationToken ct = default);

 /// <summary>
 /// 1632: if the slot holds <paramref name="incubatorId"/> and an egg hatched by <paramref name="nowUtc"/>, takes one
 /// charge, empties the slot (removes it at 0 charges) and returns the slot as it was before; null, nothing stored,
 /// otherwise.
 /// </summary>
    Task<PetIncubatorSlot?> TryCollectAsync(uint characterId, byte slot, uint incubatorId, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>
 /// Puts <paramref name="before"/> (the slot as <see cref="TryCollectAsync"/> returned it) back in place of the
 /// slot's current state. The compensation for a hatched egg that did not reach the bag, a separate write
 /// that this interface cannot make atomic with the collect.
 /// </summary>
    Task RestoreSlotAsync(uint characterId, PetIncubatorSlot before, CancellationToken ct = default);
}
