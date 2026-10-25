namespace RO2.Inventory.Domain;

/// <summary>
/// Pure (de)serialization of an item instance's socket content: a JSON array of rune item ids per slot
/// (0 = empty). Backs <see cref="InventoryItem.CardsJson"/> and <see cref="InventoryItem.SocketPunchersJson"/>.
/// </summary>
public static class ItemSockets
{
    /// <summary>Reads the per-instance socket content from a CardsJson (a JSON array of rune item ids, 0 = empty).
    /// Returns an empty array on null/blank/malformed input (never throws).</summary>
    public static uint[] ParseSockets(string? cardsJson)
    {
        if (string.IsNullOrWhiteSpace(cardsJson)) return System.Array.Empty<uint>();
        try { return System.Text.Json.JsonSerializer.Deserialize<uint[]>(cardsJson) ?? System.Array.Empty<uint>(); }
        catch { return System.Array.Empty<uint>(); }
    }

    /// <summary>Serialises socket content (rune item ids per slot, 0 = empty) back to CardsJson.</summary>
    public static string SerializeSockets(uint[] sockets)
        => System.Text.Json.JsonSerializer.Serialize(sockets);
}
