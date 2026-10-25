namespace RO2.Characters.Domain;

/// <summary>Persistence for a character's quick-slot (skill/item) bar.</summary>
public interface IQuickSlotRepository
{
    /// <summary>The character's quick-slot entries.</summary>
    Task<IReadOnlyList<CharacterQuickSlot>> GetByCharacterAsync(uint characterId);

 /// <summary>Places an entry, replacing whatever occupied that page and slot.</summary>
    Task SetAsync(CharacterQuickSlot entry);

    /// <summary>Clears the entry at the given page and slot.</summary>
    Task ClearAsync(uint characterId, byte page, byte slot);

 /// <summary>Swaps two positions, preserving whichever of them is empty.</summary>
    Task SwapAsync(uint characterId, byte pageA, byte slotA, byte pageB, byte slotB);
}
