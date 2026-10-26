using RO2.Characters.Domain;
using RO2.Inventory.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// Re-styles a character with an Appearance Change Ticket (RMI 1705): the ticket gate, the gender-specific-gear
/// gate, the persist, the mirror and the ticket consume. The host keeps the wire — the refusal block and the
/// 2329/2487/3046/2315 frames built from what this returns.
/// </summary>
public static class ChangeAppearanceFlow
{
    private const byte StorageBag = 0;
    private const byte StorageEquipped = 1;

    /// <summary>The result of an appearance change.</summary>
    public enum AppearanceOutcome
    {
        /// <summary>No character row: answer nothing.</summary>
        NoCharacter,

        /// <summary>No Appearance Change Ticket in the bag: refuse (23060) with the stored block.</summary>
        RefusedNoTicket,

        /// <summary>A gender change under a gender-specific worn item: refuse (23061) with the stored block.</summary>
        RefusedGender,

        /// <summary>The persist did not match the row: answer nothing.</summary>
        NotStored,

        /// <summary>Applied: consume the ticket and send the four frames.</summary>
        Applied,
    }

    /// <summary>Everything the host needs to log the change and build its refusal or success frames.</summary>
    public readonly record struct AppearanceResult(
        AppearanceOutcome Outcome, byte[]? StoredRaw, byte[]? NewRaw, int Remaining,
        InventoryItem? Ticket, IReadOnlyList<InventoryItem>? Worn, Character? Ch,
        bool Create0Differed, byte Create0Sent, byte Create0Kept, byte OldGender, byte NewGender);

    /// <summary>
    /// Runs the full change: loads the character and bag, finds the oldest appearance ticket, overlays the request,
    /// checks the gender gate, persists, mirrors onto the session, and consumes the ticket. The ordering (persist
    /// before mirror, mirror before consume) is preserved exactly.
    /// </summary>
    /// <param name="characterId">The character being restyled.</param>
    /// <param name="request">The 15-byte create block from the client.</param>
    /// <param name="codec">The appearance codec.</param>
    /// <param name="data">Game data, for the ticket type and worn-item gender requirement.</param>
    /// <param name="repo">The character repository.</param>
    /// <param name="inventory">The inventory repository.</param>
    /// <param name="gateway">The live session, mirrored to the new look.</param>
    /// <param name="ct">A cancellation token for the persist.</param>
    public static async Task<AppearanceResult> ChangeAsync(uint characterId, byte[] request,
        IAppearanceCodec codec, ICharacterGameData data, ICharacterRepository repo,
        IInventoryRepository inventory, IPlayerSessionGateway gateway, CancellationToken ct = default)
    {
        var ch = gateway.Snapshot ?? await repo.GetByIdAsync(characterId);
        if (ch is null)
            return new AppearanceResult(AppearanceOutcome.NoCharacter, null, null, 0, null, null, null, false, 0, 0, 0, 0);

        var items = await inventory.GetByCharacterAsync(characterId);
        var (storedRaw, storedRace) = codec.ParseStored(ch.AppearanceJson);

        var ticket = items
            .Where(i => i.StorageType == StorageBag && i.Count > 0 && data.IsAppearanceTicket(i.ItemId))
            .OrderBy(i => i.Slot)
            .FirstOrDefault();
        if (ticket is null)
            return new AppearanceResult(AppearanceOutcome.RefusedNoTicket, storedRaw, null, 0, null, null, null, false, 0, 0, 0, 0);

        var newRaw = codec.ApplyToCreateBlock(storedRaw, ch.Gender, request);
        bool create0Differed = newRaw[0] != request[0];
        byte oldGender = codec.StoredGender(storedRaw, storedRace);
        byte newGender = codec.GenderByte(newRaw);
        var worn = items.Where(i => i.StorageType == StorageEquipped).ToList();

        if (AppearanceRules.GenderChangeBlockedByWornItems(oldGender, newGender, worn.Select(i => data.RequireSex(i.ItemId))))
            return new AppearanceResult(AppearanceOutcome.RefusedGender, storedRaw, null, 0, null, worn, null,
                create0Differed, request[0], newRaw[0], oldGender, newGender);

        var (json, gender, race) = codec.Derive(newRaw);
        if (!await repo.UpdateAppearanceAsync(characterId, json, gender, race, ct))
            return new AppearanceResult(AppearanceOutcome.NotStored, storedRaw, null, 0, null, worn, null,
                create0Differed, request[0], newRaw[0], oldGender, newGender);

        ch.AppearanceJson = json; ch.Gender = gender; ch.Race = race;
        if (gateway.Snapshot is { } live && !ReferenceEquals(live, ch))
        {
            live.AppearanceJson = json; live.Gender = gender; live.Race = race;
        }
        gateway.Gender = gender;

        int remaining = ticket.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(ticket.Id); remaining = 0; }
        else { ticket.Count = remaining; await inventory.UpdateItemAsync(ticket); }

        return new AppearanceResult(AppearanceOutcome.Applied, storedRaw, newRaw, remaining, ticket, worn, ch,
            create0Differed, request[0], newRaw[0], oldGender, newGender);
    }
}
