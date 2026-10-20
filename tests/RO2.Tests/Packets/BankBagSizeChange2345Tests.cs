using System.Buffers.Binary;
using System.Text.Json;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests RMI 2345 OnBankBagSizeChange and the bank slot count (BankExpansion): the frame sequence
/// 1323, 2345, 2329, and the bank count byte at offset 121 of the login 2310 record.
/// </summary>
public class BankBagSizeChange2345Tests
{
    [Theory]
    
    
    [InlineData(0x02E2A611u, (byte)40, "010b0b0094e311a6e202000128")]
    [InlineData(0x02E38211u, (byte)60, "010b0b0094e31182e30200013c")]
    [InlineData(0x03A97711u, (byte)40, "010b0b0094e31177a903000128")]
    public void BankBagSizeChange2345_IsRetailsFrame_ByteForByte(uint handle, byte slots, string retailHex)
    {
        Assert.Equal(retailHex.ToUpperInvariant(), Convert.ToHexString(BankExpansion.BuildBankBagSizeChange2345(handle, slots)));
    }

    [Theory]
    [InlineData((byte)20, 20, (byte)40)]
    [InlineData((byte)110, 20, (byte)120)]   
    [InlineData((byte)120, 20, (byte)120)]
    public void Expansion_AddsBagSize_UpTo120(byte before, int bagSize, byte after)
    {
        Assert.Equal(after, BankExpansion.Expanded(before, bagSize));
    }

    [Fact]
    public void SlotCount_DefaultsTo20_AndIsClampedTo120()
    {
        Assert.Equal(20, BankExpansion.SlotsOf(new Character()));
        Assert.Equal(20, BankExpansion.SlotsOf(new Character { BankSlots = 0 }));
        Assert.Equal(80, BankExpansion.SlotsOf(new Character { BankSlots = 80 }));
        Assert.Equal(120, BankExpansion.SlotsOf(new Character { BankSlots = 200 }));
        Assert.Equal(20, BankExpansion.SlotsOf(null));
    }

    [Theory]
    [InlineData((byte)20, -1, (byte)20)]    
    [InlineData((byte)20, 19, (byte)20)]
    [InlineData((byte)20, 20, (byte)40)]    
    [InlineData((byte)20, 119, (byte)120)]
    [InlineData((byte)80, 30, (byte)80)]    
    [InlineData((byte)0, 5, (byte)20)]
    public void RequiredSlots_RoundTheHighestHeldCellUpToA20Step(byte stored, int highestBankSlot, byte expected)
    {
        var items = new List<InventoryItem>
        {
            new() { Id = 1, StorageType = 0, Slot = 70 },   
            new() { Id = 2, StorageType = 1, Slot = 90 },
        };
        if (highestBankSlot >= 0)
        {
            items.Add(new InventoryItem { Id = 3, StorageType = 2, Slot = 0 });
            items.Add(new InventoryItem { Id = 4, StorageType = 2, Slot = (short)highestBankSlot });
        }
        Assert.Equal(expected, BankExpansion.RequiredFor(stored, items));
    }

    [Theory]
    [InlineData((byte)20)]
    [InlineData((byte)60)]
    [InlineData((byte)120)]
    public void Head2310_Record121_IsTheBankSlotCount(byte slots)
    {
        
        var head = Head(new Character { Id = 0x7E570121, Name = "B", BankSlots = slots });
        Assert.Equal(slots, head[121]);
    }

    [Fact]
    public void Head2310_Record121_OfANeverExpandedBank_IsTheOldDonorByte()
    {
        
        
        Assert.Equal(0x14, Head(new Character { Id = 0x7E570122, Name = "B" })[121]);
    }

    [Fact]
    public void BankRecord_KeepsCellsBeyond59()
    {
        
        var record = WorldEntryService.BuildItemRecord126(new InventoryItem { Id = 77, ItemId = 60000946, StorageType = 2, Slot = 75, Count = 1 });
        Assert.Equal(5, record[15]);
        Assert.Equal(75, record[16]);
    }

    internal static byte[] Head(Character c)
    {
        using var w = new PacketWriter();
        WorldEntryService.WriteCharacterHead205(w, c, 0x0DE0B6B3A7640001UL, 0x0011A6E2u, 1100u, 0f, 0f, 0f, 0);
        return w.ToArray();
    }

    internal static string DataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
    }

    internal static IReadOnlyList<T> Table<T>(string file) =>
        JsonSerializer.Deserialize<List<T>>(File.ReadAllText(DataFile(file)))!;
}

/// <summary>
/// RMI 1348 ReqCraftListLearn and 2538 OnAckCraftLearn, and the learned recipes carried by the login 2535.
/// </summary>
public class CraftLearn1348Tests
{
    private static readonly Lazy<IReadOnlyList<CraftItemList>> Recipes =
        new(() => BankBagSizeChange2345Tests.Table<CraftItemList>("Craft_ItemList.json"));

    [Fact]
    public void Request1348_RecipeIdIsTheLastU16_AfterTheNpcHandle()
    {
        
        var body = Convert.FromHexString("010f0f0014e411b8940300131d00000a00");
        Assert.True(((ReadOnlySpan<byte>)body).TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob));
        Assert.Equal(15, blob.Length);
        Assert.Equal(10, BinaryPrimitives.ReadUInt16LittleEndian(blob[CraftListLearnHandler.RecipeIdOffset..]));
        Assert.Equal(0x13, blob[9]);
    }

    [Theory]
    
    [InlineData(new ushort[] { 10 }, "0c000000000004000100020003000a00")]
    [InlineData(new ushort[] { 10, 7 }, "0e0000000000050001000200030007000a00")]
    [InlineData(new ushort[] { 10, 7, 11 }, "100000000000060001000200030007000a000b00")]
    public void AckCraftLearn2538_IsTheWholeSet_ByteForByte(ushort[] learned, string retailHex)
    {
        var ids = CraftRecipeStore.KnownRecipeIds(Recipes.Value, 3, learned);
        Assert.Equal(retailHex.ToUpperInvariant(), Convert.ToHexString(CraftRecipeStore.BuildAckCraftLearn2538(ids)));
    }

    [Fact]
    public void KnownSet_LeavesOutLearnedRecipesOfAnotherProfession()
    {
        
        var ids = CraftRecipeStore.KnownRecipeIds(Recipes.Value, 3, new ushort[] { 10, 142 });
        Assert.Equal(new ushort[] { 1, 2, 3, 10 }, ids);
    }

    [Fact]
    public void LoginCraftIndex2535_CarriesTheLearnedRecipes()
    {
        
        var body = WorldEntryService.BuildNotifyCraftIndex2535(Recipes.Value, 3, new ushort[] { 10 });
        Assert.Equal("0A00000004000100020003000A00", Convert.ToHexString(body!));
    }

    [Fact]
    public void Refusals_FollowTheRecipeRow()
    {
        var table = Recipes.Value;
        var known = CraftRecipeStore.KnownRecipeIds(table, 3, Array.Empty<ushort>());
        CraftItemList Row(ushort id) => table.First(r => r.ID == id);

        Assert.NotNull(CraftListLearnHandler.RefusalReason(null, 60000, 3, known, 1_000_000));
        Assert.Null(CraftListLearnHandler.RefusalReason(Row(644), 644, 2, CraftRecipeStore.KnownRecipeIds(table, 2, Array.Empty<ushort>()), 1_000_000)); 
        Assert.NotNull(CraftListLearnHandler.RefusalReason(Row(142), 142, 3, known, 1_000_000));   
        Assert.NotNull(CraftListLearnHandler.RefusalReason(Row(1), 1, 3, known, 1_000_000));       
        Assert.NotNull(CraftListLearnHandler.RefusalReason(Row(10), 10, 3, known, Row(10).Learn_NeedMoney - 1));
        Assert.Null(CraftListLearnHandler.RefusalReason(Row(10), 10, 3, known, Row(10).Learn_NeedMoney));
    }
}

/// <summary>
/// RMI 1494 ReqChangeProJobID: the 2683, 2682, 2687 and 2686 answers to profession changes, the refusal
/// rules, and the stored profession.
/// </summary>
public class ChangeProJob1494Tests
{
    private static readonly Lazy<IReadOnlyList<ProJobType>> Professions =
        new(() => BankBagSizeChange2345Tests.Table<ProJobType>("ProJob_Type.json"));
    private static readonly Lazy<IReadOnlyList<ProJobChange>> ChangeItems =
        new(() => BankBagSizeChange2345Tests.Table<ProJobChange>("ProJob_Change.json"));

    [Theory]
    [InlineData((byte)4, (byte)1, "0113130039e4000000000401000000000000000000")]
    [InlineData((byte)2, (byte)50, "0113130039e4000000000232000000000000000000")]
    public void AnsChangeProJobInfo2683_IsRetailsFrame_ByteForByte(byte profession, byte level, string retailHex)
    {
        Assert.Equal(retailHex.ToUpperInvariant(),
            Convert.ToHexString(QuestProgressPackets.BuildChangeProJobInfo2683(profession, level, 0)));
    }

    [Theory]
    [InlineData(0x03FA1111u, (byte)4, (byte)1, "010e0e003ae4000000001111fa030401")]
    [InlineData(0x052A7811u, (byte)2, (byte)50, "010e0e003ae40000000011782a050232")]
    [InlineData(0x056EEC11u, (byte)4, (byte)1, "010e0e003ae40000000011ec6e050401")]
    public void NfyChangeProJob2682_ToTheChanger_IsRetailsFrame_ByteForByte(uint handle, byte profession, byte level, string retailHex)
    {
        Assert.Equal(retailHex.ToUpperInvariant(),
            Convert.ToHexString(QuestProgressPackets.BuildChangeProJob2682(handle, profession, level)));
    }

    [Fact]
    public void NfyChageProJobGuardianSkill2686_IsRetailsFrame_ByteForByte()
    {
        
        
        Assert.Equal("0400000000000000", Convert.ToHexString(QuestProgressPackets.BuildChageProJobGuardianSkill2686()));
    }

    [Fact]
    public void GuideQuests_4To2_AreRetailsThree2687s()
    {
        var pairs = ChangeProJobIDHandler.GuideQuestPairs(Professions.Value, 4, 2);
        Assert.Equal(new[] { "E83D751ED43D751E", "E93D751ED53D751E", "EA3D751ED63D751E" },
            pairs.Select(p => Convert.ToHexString(QuestProgressPackets.BuildChangeProJobQuest2687(p.Old, p.New))).ToArray());
    }

    [Fact]
    public void Refusal2683_CarriesTheCodeAtBlob18()
    {
        
        var body = QuestProgressPackets.BuildChangeProJobInfo2683(2, 1, 0, QuestProgressPackets.ChangeProJobSameProfession);
        Assert.Equal(21, body.Length);
        Assert.Equal(3, body[20]);
    }

    [Fact]
    public void ChangeResult_DecidesCodes1_3_4_FromState()
    {
        const uint me = 7;
        var rune = new InventoryItem { Id = 1, CharacterId = me, ItemId = 21330008, StorageType = 0, Count = 1 };
        byte Result(byte current, byte target, InventoryItem? item) =>
            ChangeProJobIDHandler.ChangeResult(Professions.Value, ChangeItems.Value, me, current, target, item);

        Assert.Equal(QuestProgressPackets.ChangeProJobApplied, Result(2, 4, rune));
        Assert.Equal(QuestProgressPackets.ChangeProJobUnknownProfession, Result(2, 5, rune));
        Assert.Equal(QuestProgressPackets.ChangeProJobUnknownProfession, Result(2, 0, rune));
        Assert.Equal(QuestProgressPackets.ChangeProJobSameProfession, Result(4, 4, rune));
        Assert.Equal(QuestProgressPackets.ChangeProJobMissingItem, Result(2, 4, null));
        Assert.Equal(QuestProgressPackets.ChangeProJobMissingItem,
            Result(2, 4, new InventoryItem { Id = 1, CharacterId = me + 1, ItemId = 21330008, Count = 1 }));   
        Assert.Equal(QuestProgressPackets.ChangeProJobMissingItem,
            Result(2, 4, new InventoryItem { Id = 1, CharacterId = me, ItemId = 60000946, Count = 1 }));       
        Assert.Equal(QuestProgressPackets.ChangeProJobMissingItem,
            Result(2, 4, new InventoryItem { Id = 1, CharacterId = me, ItemId = 21330008, StorageType = 2, Count = 1 })); 
    }

    private static Character Created(string createHex, byte job)
    {
        var appearance = CharacterSerializer.ParseCreateAppearance(new PacketReader(Convert.FromHexString(createHex)));
        return new Character { Id = 3, Name = "T", Job = job, MapId = 1100, AppearanceJson = CharacterSerializer.ToJson(appearance) };
    }

    [Fact]
    public void StoredProfession_Changes_AndTheClassDoesNot()
    {
        
        var c = Created("0202060dff0006000000060eff00010000000010000200", job: 16);
        WorldEntryService.SetCreationProfession(c, 4);
        Assert.Equal(4, WorldEntryService.CreationProfessionByte(c));
        Assert.Equal(16, WorldEntryService.CombatJobByte(c));
        var raw = CharacterSerializer.ParseAppearancePublic(c.AppearanceJson).RawBytes!;
        Assert.Equal("0202060DFF0006000000060EFF00010000000010000400", Convert.ToHexString(raw));
    }

    [Fact]
    public void StoredProfession_OnAnUnhealedRow_KeepsTheClass_AfterTheHeal()
    {
        
        var c = Created("02010703ff00050000000603ff00010000000010000400", job: 4);
        Assert.True(WorldEntryService.TryHealStoredCombatClass(c));
        WorldEntryService.SetCreationProfession(c, 2);
        Assert.Equal(16, WorldEntryService.CombatJobByte(c));
        Assert.Equal(2, WorldEntryService.CreationProfessionByte(c));
    }

    [Fact]
    public void StoredProfession_WithoutACreateBlock_IsPaddedAndStored()
    {
        var c = new Character { Id = 3, Name = "T", Job = 6 };
        WorldEntryService.SetCreationProfession(c, 3);
        Assert.Equal(3, WorldEntryService.CreationProfessionByte(c));
        Assert.Equal(6, WorldEntryService.CombatJobByte(c));
    }
}

/// <summary>
/// The union membership fields of the 2310 head: u32 Union_Info id at offset 173 and u64 join time in unix
/// seconds at offset 177, written from the published union membership copy. The reference join is union 101.
/// </summary>
public class UnionHead2310Tests
{
    [Fact]
    public void Head2310_CarriesThePublishedUnion()
    {
        const uint id = 0x7E570173;
        UnionMembershipStore.Publish(id, new UnionMembership(101, 0x6A6EF67B));
        try
        {
            var head = BankBagSizeChange2345Tests.Head(new Character { Id = id, Name = "U" });
            Assert.Equal("650000007BF66E6A00000000", Convert.ToHexString(head.AsSpan(173, 12)));
        }
        finally
        {
            UnionMembershipStore.Publish(id, null);
        }
    }

    [Fact]
    public void Head2310_WithoutAUnion_IsRetailsNoUnionForm()
    {
        
        const uint id = 0x7E570174;
        UnionMembershipStore.Publish(id, new UnionMembership(104, 1));
        UnionMembershipStore.Publish(id, null);
        var head = BankBagSizeChange2345Tests.Head(new Character { Id = id, Name = "U" });
        Assert.Equal("00000000FFFFFFFFFFFFFFFF", Convert.ToHexString(head.AsSpan(173, 12)));
    }
}
