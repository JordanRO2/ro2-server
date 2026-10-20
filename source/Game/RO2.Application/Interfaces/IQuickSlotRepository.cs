using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>Persistence for a character's quick-slot (skill/item) bar.</summary>
public interface IQuickSlotRepository
{
    Task<IReadOnlyList<CharacterQuickSlot>> GetByCharacterAsync(uint characterId);

 /// <summary>Places an entry, replacing whatever occupied that page and slot.</summary>
    Task SetAsync(CharacterQuickSlot entry);

    Task ClearAsync(uint characterId, byte page, byte slot);

 /// <summary>Swaps two positions, preserving whichever of them is empty.</summary>
    Task SwapAsync(uint characterId, byte pageA, byte slotA, byte pageB, byte slotB);
}
