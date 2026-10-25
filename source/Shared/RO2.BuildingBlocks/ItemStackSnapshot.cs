namespace RO2.BuildingBlocks;

/// <summary>
/// The per-instance state of an item stack, independent of where it is stored. It lets the economy contexts
/// (auction escrow, trade, shops, mail) carry an item between a bag, an escrow and a recipient without depending
/// on the game's persistence row type. The host maps it to and from its inventory row.
/// </summary>
public sealed record ItemStackSnapshot
{
    /// <summary>The item's unique instance id.</summary>
    public ulong Id { get; init; }
    /// <summary>The item's template id.</summary>
    public uint ItemId { get; init; }
    /// <summary>The stack size.</summary>
    public int Count { get; init; }
    /// <summary>The bag slot the stack occupied (-1 when it has none yet).</summary>
    public short Slot { get; init; }
    /// <summary>Refine level.</summary>
    public byte Refine { get; init; }
    /// <summary>Bestowed element.</summary>
    public byte Element { get; init; }
    /// <summary>Durability (-1 = not applicable).</summary>
    public short Durability { get; init; } = -1;
    /// <summary>Primary dye colour.</summary>
    public uint DyeColor { get; init; }
    /// <summary>Second dye colour.</summary>
    public uint DyeColor2 { get; init; }
    /// <summary>Third dye colour.</summary>
    public uint DyeColor3 { get; init; }
    /// <summary>FX-enchant index.</summary>
    public uint FxEnchantIndex { get; init; }
    /// <summary>Enchant/reinforce id.</summary>
    public ushort EnchantReinforceId { get; init; }
    /// <summary>Remaining timed-enchant seconds.</summary>
    public ushort EnchantSeconds { get; init; }
    /// <summary>Socketed cards, as JSON (null when none).</summary>
    public string? CardsJson { get; init; }
    /// <summary>Rune-hole punchers, as JSON (null when none).</summary>
    public string? SocketPunchersJson { get; init; }
    /// <summary>Whether the item is account/character bound.</summary>
    public bool Bound { get; init; }
    /// <summary>The item's own ability value (null = use its template's).</summary>
    public uint? AbilityValue { get; init; }
    /// <summary>When the item instance was created (UTC); preserved so a restored row is byte-identical.</summary>
    public DateTime CreatedAt { get; init; }
}
