namespace RO2.Characters.Domain;

/// <summary>
/// Player character domain entity.
/// </summary>
public class Character
{
    /// <summary>Race of a character created with the default appearance: 2 = Human, 1 = Noel/Ellr.</summary>
    public const byte DefaultRace = 2;

    /// <summary>Character id (the record key).</summary>
    public uint Id { get; set; }
    /// <summary>Id of the owning account.</summary>
    public uint AccountId { get; set; }
    /// <summary>Character name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Base combat class (JobInfo.ID); create[21] -&gt; charlist[73].</summary>
    public byte Job { get; set; }
    /// <summary>Appearance race; create[1] -&gt; charlist[51].</summary>
    public byte Race { get; set; } = DefaultRace;
    /// <summary>Profession (ProJob): 0 at creation, assigned in-game.</summary>
    public byte LifeJob { get; set; }
    /// <summary>Character gender.</summary>
    public byte Gender { get; set; }
    /// <summary>Base level.</summary>
    public byte Level { get; set; } = 1;
    /// <summary>Base EXP -&gt; 2310 body[70..77].</summary>
    public ulong Exp { get; set; }
    /// <summary>Job level -&gt; 2310 body[127] / charlist.</summary>
    public byte JobLevel { get; set; } = 1;
    /// <summary>Job EXP -&gt; 2310 body[128..135].</summary>
    public ulong JobExp { get; set; }
    /// <summary>
    /// Master level -&gt; 2310 body[136..137]. Nothing assigns this, so it is always 0. The builders put it in
    /// the 2310 record+140 u16 (WorldEntryService.WriteTentativeJobBlock) and in the self-2315 rec+67
    /// (AnsMapLoadingHandler), but that slot carries <see cref="EquipHideFlag"/> (see below), not a master level.
    /// </summary>
    public ushort MasterLevel { get; set; }

    /// <summary>Current map id.</summary>
    public uint MapId { get; set; }
    /// <summary>World X position.</summary>
    public float X { get; set; }
    /// <summary>World Y position.</summary>
    public float Y { get; set; }
    /// <summary>World Z position.</summary>
    public float Z { get; set; }

    /// <summary>Current HP.</summary>
    public int Hp { get; set; }
    /// <summary>Maximum HP.</summary>
    public int MaxHp { get; set; }
    /// <summary>Current MP.</summary>
    public int Mp { get; set; }
    /// <summary>Maximum MP.</summary>
    public int MaxMp { get; set; }
    /// <summary>STR primary stat (effective value: class base + bonus).</summary>
    public int Str { get; set; }
    /// <summary>AGI primary stat (effective value).</summary>
    public int Agi { get; set; }
    /// <summary>VIT primary stat (effective value).</summary>
    public int Vit { get; set; }
    /// <summary>INT primary stat (effective value).</summary>
    public int Int { get; set; }
    /// <summary>WIS primary stat (was the legacy "Dex" column; migrated field).</summary>
    public int Wis { get; set; }
    /// <summary>LUK hidden 6th stat (the client shows STR/AGI/INT/WIS/VIT), kept for later.</summary>
    public int Luk { get; set; }

    /// <summary>
    /// Allocated bonus STR. The Str/Agi/... above are the EFFECTIVE values (class base + these bonuses),
    /// recomputed on login; these deltas are the source of truth for what the player allocated via RMI 1493
    /// ReqUseBonusStatPoint. Unspent points = BonusStatPointsEarned(level) - (sum of these). Default 0.
    /// </summary>
    public int BonusStr { get; set; }
    /// <summary>Allocated bonus AGI (see <see cref="BonusStr"/>).</summary>
    public int BonusAgi { get; set; }
    /// <summary>Allocated bonus INT (see <see cref="BonusStr"/>).</summary>
    public int BonusInt { get; set; }
    /// <summary>Allocated bonus WIS (see <see cref="BonusStr"/>).</summary>
    public int BonusWis { get; set; }
    /// <summary>Allocated bonus VIT (see <see cref="BonusStr"/>).</summary>
    public int BonusVit { get; set; }
    /// <summary>Allocated bonus LUK (see <see cref="BonusStr"/>).</summary>
    public int BonusLuk { get; set; }

    /// <summary>Carried zeny.</summary>
    public long Gold { get; set; }
    /// <summary>Zeny stored in the bank (rides 2338 OnBankData at content+6).</summary>
    public long BankGold { get; set; }

    /// <summary>
    /// Save-point the character is bound to: an index into the client's game-data table 99 (RMI 1309
    /// ReqSetBindIndex / 2440 OnSavePoint). Persisted so the bind point survives relog.
    /// </summary>
    public ushort BindIndex { get; set; }

    /// <summary>
    /// Facing, as the yaw in RADIANS the client last reported (the f32 at body+68 of its C2S 1526
    /// ViewGroupPCMoveTo). The 2310 head carries it at head+107 as 2-degree steps, so a relog spawns the
    /// character facing the way it last faced. Persisted like <see cref="BindIndex"/>; 0 for documents written before it.
    /// </summary>
    public float FacingYaw { get; set; }

    /// <summary>Serialized appearance JSON.</summary>
    public string? AppearanceJson { get; set; }

    /// <summary>Default <see cref="EquipHideFlag"/> of a character that never toggled anything.</summary>
    public const ushort DefaultEquipHideFlag = 0x0006;
    /// <summary>
    /// Equip-item hide flag (u16): the costume/equipment visibility toggles the player sets with RMI 1312
    /// ReqChangeCostumeFlag. Repeated everywhere the PC is described: the char-list header [75-76] (2307), the
    /// 2310 record+140 u16, the self-2315 rec+67, and the 2380 echo of a 1312 toggle. Documents written before
    /// this field existed keep the initializer: MongoDB.Bson 3.10 leaves a member at its constructed value when
    /// the element is missing.
    /// </summary>
    public ushort EquipHideFlag { get; set; } = DefaultEquipHideFlag;

    /// <summary>
    /// "Anyone may review me": the setting the player sends with RMI 1697 ReqAllowAnyoneReviewMe, which 1696
    /// ReqReviewPC honours. Kept on the server beyond the session that set it, because the client does not
    /// re-send it at login; when off, a review is answered with the empty "not allowed" 3033. Default true.
    /// </summary>
    public bool AllowAnyoneReview { get; set; } = true;

    /// <summary>Slot count of a bank that never expanded.</summary>
    public const byte DefaultBankSlots = 20;
    /// <summary>
    /// Bank (storage) slot count. The client sizes the bank window with it; it learns it from the 2310
    /// record+121 at zone entry and from 2345 OnBankBagSizeChange after a Storage Slot Expansion item (ItemInfo
    /// Item_Type 39, Bag_Size 20). Documents written before this field existed keep the initializer;
    /// LoginPCHandler raises it for a bank that already holds items past slot 19 (the old fixed capacity was 60).
    /// </summary>
    public byte BankSlots { get; set; } = DefaultBankSlots;

    /// <summary>When the character was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>When the character was last played (UTC), or null.</summary>
    public DateTime? LastPlayed { get; set; }
    /// <summary>Total play time in seconds.</summary>
    public long PlayTimeSeconds { get; set; }
    /// <summary>Whether the character is (soft) deleted.</summary>
    public bool IsDeleted { get; set; }
    /// <summary>When the character was deleted (UTC), or null.</summary>
    public DateTime? DeletedAt { get; set; }
}
