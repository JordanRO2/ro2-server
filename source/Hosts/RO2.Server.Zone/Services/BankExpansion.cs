using System;
using System.Collections.Generic;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Slot count of the per-character bank, and how a Storage Slot Expansion item grows it.
/// </summary>
/// <remarks>
/// The count starts at 20 and grows by one 20-slot step per expansion item, up to 120. The client is told the count at login (2310 record byte 121) and when it changes (2345 OnBankBagSizeChange).
/// </remarks>
public static class BankExpansion
{
 /// <summary>Item type of the Storage Slot Expansion items. Each one adds 20 slots.</summary>
    public const byte ExpansionItemType = 39;

 /// <summary>Largest bank size. Expansion is refused at this count.</summary>
    public const byte MaxBankSlots = 120;

 /// <summary>Step that the slot count is rounded to, equal to the slots added by one expansion item.</summary>
    public const byte SlotStep = 20;

 /// <summary>Message tag of packet 2345 OnBankBagSizeChange.</summary>
    internal const ushort MsgTagBankBagSizeChange = 58260;

 /// <summary>
 /// Gets the bank size to report to the client: the stored count, or the default when none is stored, clamped to <see cref="MaxBankSlots"/>.
 /// </summary>
 /// <param name="character">Character whose bank size is read. May be null.</param>
    public static byte SlotsOf(Character? character)
    {
        var n = character?.BankSlots ?? Character.DefaultBankSlots;
        if (n == 0) return Character.DefaultBankSlots;
        return Math.Min(n, MaxBankSlots);
    }

 /// <summary>
 /// Gets the slot count after using an expansion item, capped at <see cref="MaxBankSlots"/>.
 /// </summary>
 /// <param name="current">Current slot count.</param>
 /// <param name="bagSize">Slots added by the item.</param>
    public static byte Expanded(byte current, int bagSize) =>
        (byte)Math.Clamp(current + Math.Max(0, bagSize), 0, MaxBankSlots);

 /// <summary>
 /// Gets the slot count a bank needs to hold its stored items, rounded up to the next <see cref="SlotStep"/>.
 /// </summary>
 /// <remarks>
 /// Banks written before the count was stored may hold items past slot 19. The count is raised as needed so that every stored item stays reachable.
 /// </remarks>
 /// <param name="stored">Stored slot count.</param>
 /// <param name="items">Items held by the bank. May be null.</param>
    public static byte RequiredFor(byte stored, IEnumerable<InventoryItem>? items)
    {
        var slots = stored == 0 ? Character.DefaultBankSlots : Math.Min(stored, MaxBankSlots);
        if (items is null) return slots;
        int highest = -1;
        foreach (var it in items)
            if (it.StorageType == 2 && it.Slot > highest) highest = it.Slot;
        if (highest < slots) return slots;
        int needed = (highest + 1 + SlotStep - 1) / SlotStep * SlotStep;
        return (byte)Math.Min(needed, MaxBankSlots);
    }

 /// <summary>Stores the bank slot count on the character row. Only that field is written.</summary>
 /// <param name="characters">Character repository.</param>
 /// <param name="characterId">Character to update.</param>
 /// <param name="slots">Slot count to store.</param>
    public static Task PersistAsync(ICharacterRepository characters, uint characterId, byte slots) =>
        characters.SetBankSlotsAsync(characterId, slots);

 /// <summary>
 /// Builds 2345 OnBankBagSizeChange, which tells the client the new bank slot count.
 /// </summary>
 /// <remarks>
 /// Compact layout, 13-byte body: [01 0b][u16 11][u16 msgTag][u32 handle][u8 0][u8 1][u8 slot count]. The handle is the player's own handle.
 /// </remarks>
 /// <param name="pcHandle">Handle of the player.</param>
 /// <param name="slotCount">New bank slot count.</param>
    public static byte[] BuildBankBagSizeChange2345(uint pcHandle, byte slotCount)
    {
        using var pw = new PacketWriter(13);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagBankBagSizeChange); 
            p.WriteUInt32(pcHandle);                
            p.WriteByte(0);                         
            p.WriteByte(1);                         
            p.WriteByte(slotCount);                 
        });
        return pw.ToArray();
    }
}
