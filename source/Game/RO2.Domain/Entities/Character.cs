namespace RO2.Domain.Entities;

/// <summary>
/// Player character domain entity
/// </summary>
public class Character
{
    /// <summary>Race of a character created with the default appearance: 2 = Human, 1 = Noel/Ellr.</summary>
    public const byte DefaultRace = 2;

    public uint Id { get; set; }
    public uint AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte Job { get; set; }                 // base combat class (JobInfo.ID); create[21] -> charlist[73]
    public byte Race { get; set; } = DefaultRace; // create[1] -> charlist[51]
    public byte LifeJob { get; set; }             // profession (ProJob): 0 at creation, assigned in-game
    public byte Gender { get; set; }
    public byte Level { get; set; } = 1;
    public ulong Exp { get; set; }                // base EXP -> 2310 body[70..77]
    public byte JobLevel { get; set; } = 1;       // -> 2310 body[127] / charlist
    public ulong JobExp { get; set; }             // -> 2310 body[128..135]
    // Nothing assigns this, so it is always 0. The builders put it in the 2310 record+140 u16
    // (WorldEntryService.WriteTentativeJobBlock) and in the self-2315 rec+67 (AnsMapLoadingHandler), but that
    // slot carries EquipHideFlag (see below), not a master level.
    public ushort MasterLevel { get; set; }       // -> 2310 body[136..137]

    // Position
    public uint MapId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    // Stats
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Mp { get; set; }
    public int MaxMp { get; set; }
    public int Str { get; set; }
    public int Agi { get; set; }
    public int Vit { get; set; }
    public int Int { get; set; }
    public int Wis { get; set; }   // "WIS" (was the legacy "Dex" column; migrated field)
    public int Luk { get; set; }   // hidden 6th stat (the client shows STR/AGI/INT/WIS/VIT), kept for later

    // Allocated bonus stat points per primary (spent via RMI 1493 ReqUseBonusStatPoint). The Str/Agi/...
    // above are the EFFECTIVE values (class base + these bonuses), recomputed on login; these deltas are
    // the source of truth for what the player allocated. Unspent points = BonusStatPointsEarned(level) -
    // (sum of these). Default 0. See StatService.BonusStatPointsEarned / MaxStatPerStat / BonusStatCost.
    public int BonusStr { get; set; }
    public int BonusAgi { get; set; }
    public int BonusInt { get; set; }
    public int BonusWis { get; set; }
    public int BonusVit { get; set; }
    public int BonusLuk { get; set; }

    // Currency
    public long Gold { get; set; }
    public long BankGold { get; set; }   // zeny stored in the bank (rides 2338 OnBankData at content+6)

    // Save-point the character is bound to: an index into the client's game-data table 99 (RMI
    // 1309 ReqSetBindIndex / 2440 OnSavePoint). Persisted so the bind point survives relog.
    public ushort BindIndex { get; set; }

    // Facing, as the yaw in RADIANS the client last reported (the f32 at body+68 of its C2S 1526
    // ViewGroupPCMoveTo). The 2310 head carries it at head+107 as 2-degree steps, so a relog spawns the
    // character facing the way it last faced. Persisted like BindIndex; 0 for documents written before it.
    public float FacingYaw { get; set; }

    // Appearance
    public string? AppearanceJson { get; set; }

    // Equip-item hide flag (u16): the costume/equipment visibility toggles the player sets with RMI 1312
    // ReqChangeCostumeFlag. It is kept per character and repeated everywhere the PC is described: the
    // char-list header [75-76] (2307) and the 2310 record+140 u16 carry the same value, the self-2315 rec+67
    // repeats it, and the 2380 echo of a 1312 toggle carries the new value. 0x0006 is the value of a
    // character that never toggled anything. Documents written before this field existed keep the
    // initializer: MongoDB.Bson 3.10 leaves a member at its constructed value when the element is missing.
    public const ushort DefaultEquipHideFlag = 0x0006;
    public ushort EquipHideFlag { get; set; } = DefaultEquipHideFlag;

    // "Anyone may review me": the setting the player sends with RMI 1697 ReqAllowAnyoneReviewMe, which 1696
    // ReqReviewPC honours (AllowAnyoneReviewMeHandler / ReviewPCHandler). It is kept on the server beyond the
    // session that set it, because the client does not re-send it at login; when it is off, a review is
    // answered with the empty "not allowed" 3033. Default true (most characters allow reviews). Documents
    // written before this field existed keep the initializer (see EquipHideFlag).
    public bool AllowAnyoneReview { get; set; } = true;

    // Bank (storage) slot count. The client keeps it in its bank manager and sizes the bank window with it;
    // it learns it from the 2310 record+121 at zone entry and from 2345 OnBankBagSizeChange after a Storage
    // Slot Expansion item (ItemInfo Item_Type 39, Bag_Size 20). See RO2.Server.Zone.Services.BankExpansion.
    // 20 is what a character that never expanded carries. Documents written before this field existed keep
    // the initializer (see EquipHideFlag); LoginPCHandler raises it for a bank that already holds items past
    // slot 19 (the old fixed capacity was 60).
    public const byte DefaultBankSlots = 20;
    public byte BankSlots { get; set; } = DefaultBankSlots;

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayed { get; set; }
    public long PlayTimeSeconds { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}
