namespace RO2.GameData;

/// <summary>
/// Item master data from ItemInfo.xlsx (18,428 records)
/// </summary>
public sealed record ItemData
{
    public uint ID { get; init; }
    public string Name { get; init; } = "";
    public byte Item_Type { get; init; }
    // Item_Type-specific option (generic; some item types store large/negative values, so it is int).
    // For the immediate-refine stones (Item_Type 66 = weapon, 67 = armor) it is the target +N refine
    // level (5/8/10/15/17/20).
    public int Item_Type_Option { get; init; }
    public byte Item_Category { get; init; }
    public byte Grade { get; init; }
    public uint Price_Buy { get; init; }
    public uint Price_Sell { get; init; }
    public byte Stack_Max { get; init; }
    public byte Require_Level { get; init; }
    public byte Require_Sex { get; init; }
    public long Require_Job { get; init; }
    public ushort Equip_Type { get; init; }
    public ushort Equip_Slot { get; init; }
    // Mask of the costume layers the item covers (read by EquipRules); 0 = none, max value in ItemInfo is 249.
    public byte Equip_Slot_Overlap { get; init; }
    public byte Weapon_Type { get; init; }
    public byte Armor_Type { get; init; }
    // Number of inventory slots this item adds when equipped into the character bag window (Item_Type 7 =
    // bag). 0 for non-bag items. Total usable inventory = base + Σ Bag_Size of the equipped bags.
    public int Bag_Size { get; init; }
    public byte Bind_Type { get; init; }
    public uint Durability { get; init; }
    // ItemInfo Abili_val: the base ability value every item record carries at record+69 (u32), see
    // WorldEntryService.RecordAbilityValue.
    public int Abili_val { get; init; }
    public uint Set_ID { get; init; }
    public uint Socket_GroupID { get; init; }
    // The RandomSet.json ID whose rows are this item's random set (0 = none). The item-change exchange
    // (1584 -> 2851) gains an entry of the key item's set (ExtendCacheChangeItemHandler).
    public uint RandomSet_ID { get; init; }
    public uint Effect_ID_1 { get; init; }
    public uint Skill_ID_1 { get; init; }
    public byte Is_Drop { get; init; }
    public byte Is_Trade { get; init; }
    public byte Is_Sell { get; init; }
    public byte High_Category { get; init; }
    public byte Medium_Category { get; init; }
    public string Icon { get; init; } = "";
}

/// <summary>
/// Map master data from Map_List.xlsx (72 records)
/// </summary>
public sealed record MapData
{
    public uint Map_Index { get; init; }
    public string Map_Name { get; init; } = "";
    public string Map_File { get; init; } = "";
    public byte Map_Type { get; init; }
    public ushort Max_Count { get; init; }
    public uint Map_ID { get; init; }
    public float x { get; init; }
    public float y { get; init; }
    public float z { get; init; }
    public byte Map_PK { get; init; }
    public byte Mount { get; init; }
    public byte Attack { get; init; }
    public byte Skill { get; init; }
    public uint Map_Exp { get; init; }
}

/// <summary>
/// Quest master data from Quest_Info.xlsx (1,460 records)
/// </summary>
public sealed record QuestData
{
    public uint Quest_ID { get; init; }
    public byte Quest_Type { get; init; }
    public uint Area_ID { get; init; }
    public byte Start_Type { get; init; }
    public uint Start_Type_ID { get; init; }
    public uint Reward_NPC_ID { get; init; }
    public byte Recommand_Base_Level { get; init; }
    public byte MIN_Base_Level { get; init; }
    public byte MAX_Base_Level { get; init; }
    public int PC_Job { get; init; }
    public int Reward_Money { get; init; }
    public int Reward_EXP { get; init; }
    // Diamonds the row pays. Loaded for completeness; the 2429 still reports 0 because this server
    // keeps no diamond balance (see QuestPrizeHandler.GrantRewardsAsync).
    public int Reward_Dia { get; init; }

    // How the seven Reward_Item slots are paid: 0 = no item, 1 = every filled slot, 2 and 5 = only the slot
    // the player picked (these quests can offer 3 to 7 items, so slots 3-7 must be loaded too).
    public byte Reward_Type { get; init; }
    public uint Reward_Item_ID1 { get; init; }
    public uint Reward_Item_Count1 { get; init; }
    public uint Reward_Item_ID2 { get; init; }
    public uint Reward_Item_Count2 { get; init; }
    public uint Reward_Item_ID3 { get; init; }
    public uint Reward_Item_Count3 { get; init; }
    public uint Reward_Item_ID4 { get; init; }
    public uint Reward_Item_Count4 { get; init; }
    public uint Reward_Item_ID5 { get; init; }
    public uint Reward_Item_Count5 { get; init; }
    public uint Reward_Item_ID6 { get; init; }
    public uint Reward_Item_Count6 { get; init; }
    public uint Reward_Item_ID7 { get; init; }
    public uint Reward_Item_Count7 { get; init; }

    /// <summary>
    /// The seven reward slots in column order, empty ones included, so that array index i is the
    /// 0-based pick a choice turn-in sends at 1378 body+18 (index 0 = Reward_Item_ID1).
    /// </summary>
    public (uint Id, uint Count)[] RewardItemSlots() => new[]
    {
        (Reward_Item_ID1, Reward_Item_Count1),
        (Reward_Item_ID2, Reward_Item_Count2),
        (Reward_Item_ID3, Reward_Item_Count3),
        (Reward_Item_ID4, Reward_Item_Count4),
        (Reward_Item_ID5, Reward_Item_Count5),
        (Reward_Item_ID6, Reward_Item_Count6),
        (Reward_Item_ID7, Reward_Item_Count7),
    };

    public uint Next_Quest_ID { get; init; }

    // What the server does when the quest is accepted. Action_Type 3 hands the player Action_Value2 of
    // item Action_Value1: the 2421 that accepts an Action_Type-3 quest is followed by a 2333 of exactly
    // that item and count. A quest with several Quest_Info rows keeps only its FIRST row here
    // (GameDataStore's TryAdd), so the grants on later rows are not loaded.
    public byte Action_Type { get; init; }
    public uint Action_Value1 { get; init; }
    public uint Action_Value2 { get; init; }

    // Completion objective. Complete_Type selects the objective kind; the values are its parameters.
    //   1 = talk to NPC (Value1 = NPC id)
    //   2 = KILL monster (Value1 = monster/NPC id, Value2 = how many to kill)
    //   4 = collect item  (Value1 = item id, Value2 = how many)
    //   5 = deliver item  (Value1 = item id, Value2 = how many, Value3 = the NPC it goes to: an NPCInfo id).
    //       When the same row also carries Action_Type 3 of the same item, Action_Value2 == Value2, so the
    //       accept grant alone meets the objective.
    // For Complete_Type==2, Value1 is an NPCInfo id.
    public byte Complete_Type { get; init; }
    public uint Complete_Type_Value1 { get; init; }
    public uint Complete_Type_Value2 { get; init; }
    public uint Complete_Type_Value3 { get; init; }
}

/// <summary>
/// Skill master data from Skill_Table_Base.xlsx (2,736 records)
/// </summary>
public sealed record SkillData
{
    public uint Skill_Group { get; init; }
    public string Name { get; init; } = "";
    public ushort UI_Property { get; init; }
    public byte Max_Level { get; init; }
    public uint CoolTime_Group { get; init; }
    public uint Skill_Damage_Base { get; init; }
    public uint Skill_Damage_Add { get; init; }
    public byte Need_Base_Level { get; init; }
    public uint Need_Before_Skill_ID_1 { get; init; }
    public uint Need_Before_Skill_ID_2 { get; init; }
    public uint Need_Before_Skill_ID_3 { get; init; }
    public byte Equip_Weapon { get; init; }
}

/// <summary>
/// Per-level skill data from Skill_Table_SkillLevel.xlsx
/// Keyed by unique ID (Skill_Group * 100 + Skill_Level)
/// </summary>
public sealed record SkillLevelData
{
    public uint ID { get; init; }
    public uint Skill_Group { get; init; }
    public byte Skill_Level { get; init; }
    public int Use_SP { get; init; }
    public int Use_HP { get; init; }
    public float Casting_Time { get; init; }
    public float Cooldown_Time { get; init; }
    public float DamageRate { get; init; }
    public float Effect_Duration { get; init; }
    public int Need_Skill_Money { get; init; }
    // Item cost to learn/level this skill (production skills only; 0 = free). The learn handler
    // must have >= Number_Of_Spended_Item of Spended_Item_ID and consume them on success.
    public uint Spended_Item_ID { get; init; }
    public int Number_Of_Spended_Item { get; init; }
}

/// <summary>
/// NPC master data from NPCInfo.xlsx (3,559 records)
/// </summary>
public sealed record NpcData
{
    public uint ID { get; init; }
    public uint String_Name_ID { get; init; }
    public int Camp { get; init; }
    public int Min_Level { get; init; }
    public int Max_Level { get; init; }
    public int Family { get; init; }
    public int Grade { get; init; }
    public int Race { get; init; }
    public int Element { get; init; }
    public float Walk_Speed { get; init; }
    public float Run_Speed { get; init; }
    public float Attack_Range { get; init; }
    public float Attack_Cooldown_Time { get; init; }
    // Horizontal radius within which this NPC reacts to a nearby player. Typically 9 units on hostiles and
    // also present on passive assets (e.g. Poring 11004320+ carries 9-15), so it is the SIGHT radius, not an
    // aggression flag: whether a monster acts on a player in this radius is NpcAggression (server-side, custom).
    public float Reaction_Bound { get; init; }
    public float Trace_Bound { get; init; }
    public uint SkillGroup { get; init; }
    public uint Drop_ID { get; init; }
    public float Base_Exp_Multiplier { get; init; }
    public int Invincibility { get; init; }
    // Seconds an emptied corpse stays before it despawns (2507 OnNPC_Out). Values 0/1/2/5/10/25/30 in
    // NPCInfo.json.
    public float Corpse_Disappear_Time { get; init; }
    // FuncObjectInfo id of the gathering object this NPC's corpse becomes; 0 = a plain corpse. All
    // FuncObject_Type 9. It decides whether a death sends 2534 OnNotifyCanGatherDeadNpc: only NPCs with a
    // non-zero value here send one.
    public uint AfterDeath_FuncObject_ID { get; init; }

    // Function slots wire an NPC to its capabilities. Function_Type 11 = "opens the shop of the
    // merchant named by Function_ID" (which may be this NPC itself or a different merchant NPC — e.g.
    // field NPC 11130000 has Function_Type_3=11, Function_ID_3=10090000, so it opens the Kafra's
    // shop). This is why a shop cannot be found by the NPC's own id alone.
    public int Function_Type_1 { get; init; }
    public uint Function_ID_1 { get; init; }
    public int Function_Type_2 { get; init; }
    public uint Function_ID_2 { get; init; }
    public int Function_Type_3 { get; init; }
    public uint Function_ID_3 { get; init; }
}

/// <summary>
/// Server-side NPC stats (computed from Level + Grade).
/// Editable in NpcServerInfo.xlsx - NOT from the client.
/// </summary>
public sealed record NpcServerStats
{
    public uint NPC_ID { get; init; }
    public int Level { get; init; }
    public int Grade { get; init; }
    public int HP { get; init; }
    public int ATK { get; init; }
    public int MATK { get; init; }
    public int DEF { get; init; }
    public int MDEF { get; init; }
    public int EXP_Reward { get; init; }
    public uint Drop_ID { get; init; }
    public int Camp { get; init; }
    public int Element { get; init; }
    public float Walk_Speed { get; init; }
    public float Run_Speed { get; init; }
}

/// <summary>
/// NPC grade multiplier table (editable in NpcGradeStats.xlsx).
/// </summary>
public sealed record NpcGradeMultiplier
{
    public int Grade { get; init; }
    public string Name { get; init; } = "";
    public float HP_Multiplier { get; init; }
    public float ATK_Multiplier { get; init; }
    public float DEF_Multiplier { get; init; }
    public float EXP_Multiplier { get; init; }
    public float Drop_Multiplier { get; init; }
}

/// <summary>
/// Monster drop table entry (editable in DropTable.xlsx).
/// Drop_Rate is per 10000 (5000 = 50%).
/// Item_ID = 0 means gold drop (Min_Count/Max_Count = gold range).
/// </summary>
public sealed record DropTableEntry
{
    public uint Drop_ID { get; init; }
    public int Slot { get; init; }
    public uint Item_ID { get; init; }
    public int Min_Count { get; init; }
    public int Max_Count { get; init; }
    public int Drop_Rate { get; init; }
    public int Broadcast { get; init; }
    public string Comment { get; init; } = "";
}

/// <summary>
/// NPC level scaling base stats (editable in NpcLevelScaling.xlsx).
/// </summary>
public sealed record NpcLevelScalingEntry
{
    public int Level { get; init; }
    public int Base_HP { get; init; }
    public int Base_ATK { get; init; }
    public int Base_MATK { get; init; }
    public int Base_DEF { get; init; }
    public int Base_MDEF { get; init; }
    public int Base_EXP { get; init; }
}

/// <summary>
/// EXP table from BaseExpTable.xlsx (50 records)
/// </summary>
public sealed record ExpTableEntry
{
    public byte Base_Level { get; init; }
    public long Base_Exp_Next { get; init; }
    public int NPC_Base_Exp { get; init; }
}

/// <summary>
/// NPC shop entry from Merchant.xlsx (2,610 records)
/// Maps NPC ID → list of items they sell
/// </summary>
public sealed record MerchantEntry
{
    public int No { get; init; }
    public uint ID { get; init; }         // NPC ID
    public System.Text.Json.JsonElement NPC_Name { get; init; }
    public int Buy_Type { get; init; }
    public uint Item { get; init; }       // Item ID sold
    public int Max { get; init; }
}

/// <summary>
/// Position/spawn data from Position_Info.xlsx (229 records)
/// </summary>
public sealed record PositionData
{
    public uint Position_ID { get; init; }
    public uint Area_ID { get; init; }    // Map ID
    public float Position_X { get; init; }
    public float Position_Y { get; init; }
    public float Position_Z { get; init; }
    public float Radius { get; init; }
    public int Direction { get; init; }
    public string Category { get; init; } = "";
    public string name { get; init; } = "";
}

/// <summary>
/// NPC/Monster spawn entry from spawn.tbl files (30,077 records across 135 maps)
/// </summary>
public sealed record SpawnEntry
{
    public uint MapId { get; init; }
    public int Index { get; init; }
    public float PosX { get; init; }
    public float PosY { get; init; }
    public float PosZ { get; init; }
    public float Rot { get; init; }
    public uint AssetIndex { get; init; }   // NPC/monster ID from NPCInfo
    public float HoverRadius { get; init; }
    public int Triggered { get; init; }
    public float SpawnRadius { get; init; }
}

/// <summary>
/// Teleport destination from Teleport.xlsx (16 records)
/// </summary>
public sealed record TeleportData
{
    public uint Index { get; init; }
    public uint Map_Number { get; init; }
    public float Location_X { get; init; }
    public float Location_Y { get; init; }
    public float Location_Z { get; init; }
    public int Cost { get; init; }
    public byte Need_Level { get; init; }
}
