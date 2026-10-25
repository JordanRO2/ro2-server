namespace RO2.Characters.Domain;

/// <summary>
/// One entry on a character's quick-slot bar (the skill/item bar).
/// </summary>
/// <remarks>
/// <para>
/// The bar was not persisted at all before: the quick-slot handlers acknowledged the request and
/// dropped it, so anything placed on the bar vanished on relog. Retail's ack (2473/2474/2475)
/// echoes nothing back — the client keeps its own view and the server is expected to remember it.
/// </para>
/// <para>
/// Field meanings come from the client's 1319 request frames, blob-relative:
/// <c>+8</c> page (3 values: 0,1,2), <c>+9</c> slot within the page (10 values: 0..9),
/// <c>+10</c> entry type (3 values: 1,2,3), <c>+12</c> u32 skill/item id,
/// <c>+16</c> u16 count-or-level, <c>+20</c> u64 item uid which is zero —
/// i.e. present only when the entry is an item rather than a skill.
/// </para>
/// <para>
/// ⚠️ The three <see cref="EntryType"/> values are recorded, not interpreted: 2 is by far the most common,
/// 1 is common and 3 is rare, and nothing here establishes which is skill and which is item.
/// The server stores the byte and gives it back, so it does not need to know.
/// </para>
/// </remarks>
public class CharacterQuickSlot
{
    /// <summary>Row id (storage key).</summary>
    public ulong Id { get; set; }
    /// <summary>Owning character id.</summary>
    public uint CharacterId { get; set; }

 /// <summary>Bar page, 0..2.</summary>
    public byte Page { get; set; }

 /// <summary>Slot within the page, 0..9.</summary>
    public byte Slot { get; set; }

 /// <summary>Entry kind as the client sends it. Stored verbatim, not interpreted.</summary>
    public byte EntryType { get; set; }

 /// <summary>Skill or item id.</summary>
    public uint EntryId { get; set; }

 /// <summary>Count for stacks, or skill level. Meaning not established.</summary>
    public ushort Amount { get; set; }

 /// <summary>Item uid; zero when the entry is not an item.</summary>
    public ulong ItemUid { get; set; }
}
