namespace RO2.Guilds.Domain;

/// <summary>One persisted guild member, keyed by the character's 64-bit GID.</summary>
public sealed class GuildMember
{
    /// <summary>Lowest level a member can have; the level of a member stored without one.</summary>
    public const byte MinLevel = 1;

    /// <summary>Character id of the member.</summary>
    public uint CharId { get; set; }
    /// <summary>The member's 64-bit GID, which keys the member and matches the character's own login GID.</summary>
    public ulong Gid { get; set; }
    /// <summary>Character name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Character level.</summary>
    public byte Level { get; set; } = MinLevel;
    /// <summary>Character job id.</summary>
    public byte Job { get; set; }
 /// <summary>Rank: the index of a title in the guild's rank table. Index 0 is the master's title.</summary>
    public byte AuthId { get; set; }
 /// <summary>
 /// Guild EXP this member has contributed over time. Credited with each donation and handed to the master when the
 /// member leaves.
 /// </summary>
    public long ContribPoints { get; set; }
 /// <summary>
 /// The member's EXP donation rate in percent, set by the member and kept across sessions. It is the share of each
 /// quest prize's EXP that is donated to the guild. The default 0 donates nothing.
 /// </summary>
    public byte BribePercent { get; set; }
 /// <summary>
 /// When the member joined. Null for members stored before this was recorded.
 /// </summary>
    public DateTime? JoinedUtc { get; set; }
}

/// <summary>One title of a guild's rank table.</summary>
public sealed class GuildRank
{
 /// <summary>Rank index, from 0 (master) to 9.</summary>
    public byte Index { get; set; }
 /// <summary>The title as sent to the client.</summary>
    public string Name { get; set; } = string.Empty;
 /// <summary>Permission bits of the title.</summary>
    public byte Permissions { get; set; }
}

/// <summary>One item parked in the guild vault. The full item instance is kept, so a withdraw restores it exactly.</summary>
public sealed class GuildStorageItem
{
    /// <summary>Durability value of an item that has none (not wearable gear).</summary>
    public const short NoDurability = -1;

    /// <summary>Unique id of the item instance.</summary>
    public ulong Uid { get; set; }
    /// <summary>Item template id.</summary>
    public uint ItemId { get; set; }
    /// <summary>Stack size.</summary>
    public int Count { get; set; } = 1;
    /// <summary>Refine level.</summary>
    public byte Refine { get; set; }
    /// <summary>Durability, or NoDurability for an item without one.</summary>
    public short Durability { get; set; } = NoDurability;
    /// <summary>Socketed cards as JSON, or null.</summary>
    public string? CardsJson { get; set; }
    /// <summary>Honed grinding lines (RMI 1676) as JSON; null when the item was never honed.</summary>
    public string? GrindingJson { get; set; }
    /// <summary>Imprinted seal lines as JSON; null when the item was never sealed.</summary>
    public string? SealJson { get; set; }
 /// <summary>
 /// Remaining instance state of the item: bestowed element, dye colour and rune-hole punchers. Vault items stored
 /// before these fields existed load with neutral values.
 /// </summary>
    public byte Element { get; set; }
    /// <summary>First dye colour.</summary>
    public uint DyeColor { get; set; }
    /// <summary>Rune-hole punchers as JSON, or null.</summary>
    public string? SocketPunchersJson { get; set; }
 /// <summary>
 /// Remaining per-instance state of the item (second and third dye colours, enchant data and bind history),
 /// carried for the same reason. Rows stored before these existed load with zero or false.
 /// </summary>
    public uint DyeColor2 { get; set; }
    /// <summary>Third dye colour.</summary>
    public uint DyeColor3 { get; set; }
    /// <summary>Enchant effect index.</summary>
    public uint FxEnchantIndex { get; set; }
    /// <summary>Enchant reinforce id.</summary>
    public ushort EnchantReinforceId { get; set; }
    /// <summary>Enchant seconds.</summary>
    public ushort EnchantSeconds { get; set; }
    /// <summary>Whether the item is bound.</summary>
    public bool Bound { get; set; }
 /// <summary>
 /// The item's own ability value, or null to use the template's value. Rows stored before this existed load with
 /// null.
 /// </summary>
    public uint? AbilityValue { get; set; }
 /// <summary>Vault slot index, from 0 to N.</summary>
    public byte StorageSlot { get; set; }
    /// <summary>Character that deposited the item.</summary>
    public uint DepositedByCharId { get; set; }
}

/// <summary>
/// A guild aggregate. It is persisted as one document and is the live in-memory object that every handler
/// modifies.
/// </summary>
public sealed class Guild
{
    /// <summary>Length in bytes of <see cref="Guid16"/>.</summary>
    public const int GuidLength = 16;

    /// <summary>Process-allocated guild id; stored as the document key.</summary>
    public uint Id { get; set; }
 /// <summary>The 16-byte key the client uses to address the guild.</summary>
    public byte[] Guid16 { get; set; } = new byte[GuidLength];
    /// <summary>Guild name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Character id of the guild master.</summary>
    public uint MasterCharId { get; set; }
    /// <summary>Guild notice text.</summary>
    public string Notice { get; set; } = string.Empty;
    /// <summary>
    /// The trailing colour-code character (U+F800..U+FFFF) the master chose for <see cref="Notice"/>, kept apart from the
    /// text and echoed after it on the 2559 and 2564 notice. 0 = no colour set (default text): nothing is sent after the text.
    /// </summary>
    public ushort NoticeColorCode { get; set; }
    /// <summary>Guild funds.</summary>
    public long Gold { get; set; }
    /// <summary>
    /// Item id of the robe the guild wears, or 0 when it has never bought one and wears the default robe. Set when the
    /// master picks a robe in the robe window.
    /// </summary>
    public uint RobeItemId { get; set; }
    /// <summary>
    /// The robe's colour, assigned at random each time the master picks a robe, or 0 for the default robe's colour.
    /// </summary>
    public uint RobeColorCode { get; set; }
 /// <summary>When the guild was created. Null for guilds created before this was recorded.</summary>
    public DateTime? CreatedUtc { get; set; }
 /// <summary>
 /// Lifetime guild EXP, summed over every credit. The guild level and the EXP within that level are derived from it
 /// on every use, so the two cannot drift apart. Guilds stored before this field existed load with 0.
 /// </summary>
    public ulong Exp { get; set; }
 /// <summary>The guild's rank table, seeded with the default titles on creation and on load.</summary>
    public List<GuildRank> Ranks { get; set; } = new();
    /// <summary>The guild's members.</summary>
    public List<GuildMember> Members { get; set; } = new();
    /// <summary>The guild vault.</summary>
    public List<GuildStorageItem> Storage { get; set; } = new();
    /// <summary>Learned guild skill ids.</summary>
    public List<uint> Skills { get; set; } = new();

 /// <summary>
 /// Lock that serialises every read and write of this guild's lists and fields. It is process state and is not
 /// stored.
 /// </summary>
    [System.Text.Json.Serialization.JsonIgnore] public Lock Gate { get; } = new();
}

/// <summary>A pending guild invite, kept until the invitee answers.</summary>
public sealed class GuildPendingInvite
{
    /// <summary>Inviting guild.</summary>
    public uint GuildId { get; set; }
    /// <summary>Character that sent the invite.</summary>
    public uint InviterCharId { get; set; }
 /// <summary>
 /// The 16-byte token of the invite popup. The invitee's answer must echo the token of the latest invite.
 /// </summary>
    public byte[] Token { get; set; } = Array.Empty<byte>();
    /// <summary>When the invite was sent.</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
