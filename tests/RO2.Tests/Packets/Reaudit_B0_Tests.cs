using System.Buffers.Binary;
using System.Text.Json;
using RO2.Domain.Entities;
using RO2.GameData.Tables;
using RO2.Network.Packets;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>Layout tests for 2588 on the glider burst, the 2310 vitals, skill list, hide flag and titles, the 2375 record of NPC-bought stacks, the Khara titles, the 2498 regeneration amounts and the 2488 guild-link burst.</summary>
/// <remarks>Tests with a reference frame name it; the others pin the layout the client reader expects.</remarks>
public class Reaudit_B0_Tests
{
    private static readonly JsonSerializerOptions TableJson = new() { PropertyNameCaseInsensitive = true };

    private static string DataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", name)))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
        return Path.Combine(dir.FullName, "data", name);
    }

    private static List<T> Table<T>(string name) =>
        JsonSerializer.Deserialize<List<T>>(File.ReadAllText(DataFile(name)), TableJson)
        ?? throw new InvalidDataException(name);

    private static readonly Lazy<List<PCBaseStatus>> PcBase = new(() => Table<PCBaseStatus>("PCBaseStatus.json"));
    private static readonly Lazy<List<KharaInfo>> Khara = new(() => Table<KharaInfo>("Khara_Info.json"));
    private static readonly Lazy<IReadOnlyDictionary<uint, GuildSkillLevel>> GuildTable =
        new(() => GuildSkillLearnPackets.Index(Table<GuildSkillLevel>("Guild_Skill_Level.json")));

    
    [Fact]
    public void Card2588_MatchesTheRetailWarpBurstFrame()
    {
        
        
        
        Assert.Equal(Convert.FromHexString("0a0000000100a793540102000000"), CardPackets.BuildRegistCardBook2588(22320039));
    }

    
    [Theory]
    
    [InlineData(1, 1, 2, 1)]          
    [InlineData(23, 8, 3, 1)]         
    [InlineData(103, 34, 8, 2)]       
    [InlineData(251, 251, 17, 12)]    
    [InlineData(516, 31, 32, 2)]      
    [InlineData(1809, 481, 110, 22)]  
    [InlineData(2978, 601, 180, 28)]  
    [InlineData(3574, 600, 216, 28)]  
    [InlineData(5486, 369, 331, 17)]  
    public void Regen_IsTheClassFormulaOfTheSheetsVitAndWis(int vit, int wis, int hp, int sp)
    {
        foreach (var row in PcBase.Value)
        {
            var (h, s) = WorldEntryService.RegenAmounts(row, vit, wis, null);
            Assert.Equal((ushort)hp, h);
            Assert.Equal((ushort)sp, s);
        }
    }

    [Fact]
    public void Regen_DecimalCoefficient_DoesNotFloorOneLowOnAWholeProduct()
    {
        
        var (h, _) = WorldEntryService.RegenAmounts(PcBase.Value[0], 50, 1, null);
        Assert.Equal((ushort)5, h);
    }

    [Fact]
    public void Regen_AddsTheWornHpAndSpRestoreTraits()
    {
        var gear = new StatService.EquipStats(new Dictionary<byte, int> { [28] = 40, [29] = 7 }, new Dictionary<byte, float>());
        var (h, s) = WorldEntryService.RegenAmounts(PcBase.Value[0], 251, 251, gear);
        Assert.Equal((ushort)(17 + 40), h);
        Assert.Equal((ushort)(12 + 7), s);
        Assert.Equal(((ushort?)null, (ushort?)null), WorldEntryService.RegenAmounts((PCBaseStatus?)null, 251, 251, gear));
    }

    [Fact]
    public void PcTrait2498_RegenSlots_RebuildTheRetailFrame()
    {
        
        
        var retail = Convert.FromHexString(
            "028c008c00cce3000000002a1a00006e1263015f015f01fb00fb0000000000000000000000770100000000520800003a080000000013" +
            "01000001000000000000000000000000000000803f2a1a00006e1211000c0000000000000022046001be02c202000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000");
        var blob = retail.AsSpan(3).ToArray();
        uint maxHp = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8));
        ushort maxSp = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(12));
        uint curHp = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(72));
        ushort curSp = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(76));
        float range = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(68));
        ushort points = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(38));
        int wis = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(20)), vit = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(22));
        blob.AsSpan(TraitPackets.PcTraitHpRegenOffset, 4).Clear();

        var (h, s) = WorldEntryService.RegenAmounts(PcBase.Value[0], vit, wis, null);
        var built = TraitPackets.BuildPcTrait2498(maxHp, maxSp, curHp, curSp, range, blob,
            bonusStatPoints: points, hpRegen78: h, spRegen80: s);

        Assert.Equal(retail, built);
    }

    
    
    
    private const string RetailVitals81 = "90a30000be350000";
    private const string RetailHide140 = "5703";
    private const string RetailVec1 =
        "1c00354f09006a5009002d510900f35109001d530900117709007a770900d777090042780900a17809000379090067790900cd790900" +
        "317a0900937a090001093d0067093d00cd093d00950a3d00fb0a3d005f0b3d00c30b3d00270c3d008e0c3d00f20c3d00560d3d00" +
        "ba0d3d0083e74c00";

    private static byte[] Build2310(Character c, (uint, ushort, uint, ushort)? vitals, IReadOnlyList<(uint, byte)>? skills)
    {
        using var w = new PacketWriter();
        WorldEntryService.WriteCharacterDetail(w, c, 0x0DE0B6B3A7F07304UL, 0x0137B811u, null, 1100u, 10f, 20f, 30f,
            vitals: vitals, skillList: skills);
        return w.ToArray();
    }

    [Fact]
    public void Detail2310_VitalsHideFlagAndSkillList_MatchRetail()
    {
        var vec1 = Convert.FromHexString(RetailVec1);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(vec1);
        
        var skills = Enumerable.Range(0, n)
            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(vec1.AsSpan(2 + 4 * i)))
            .Select(id => (id / 100, (byte)(id % 100))).Reverse().ToList();
        var c = new Character { Id = 0x7FB0_2310, Name = "Vitals", Level = 50, EquipHideFlag = 0x0357 };

        var body = Build2310(c, (41872u, (ushort)13758, 41872u, (ushort)13758), skills);

        Assert.Equal(RetailVitals81, Convert.ToHexString(body.AsSpan(4 + 81, 8)).ToLowerInvariant());
        Assert.Equal(RetailHide140, Convert.ToHexString(body.AsSpan(4 + 140, 2)).ToLowerInvariant());
        Assert.Equal(RetailVec1, Convert.ToHexString(body.AsSpan(4 + 205, vec1.Length)).ToLowerInvariant());
    }

    [Fact]
    public void PcRecordPlus67_IsTheHideFlagThe2310Carries_NotMasterLevel()
    {
        
        
        
        
        var c = new Character { Id = 0x7FB0_2315, Name = "Hide", Level = 50, Gender = 1, Race = 1, Job = 1, EquipHideFlag = 0x0357, MasterLevel = 0 };
        var p = new PlayerState { EntityId = 0x0137B811u, CharacterId = c.Id, CharacterUid = 0x0DE0B6B3A7F07304UL, Name = "Hide", Character = c, MapId = 1100 };

        var detail = Build2310(c, null, null);
        Assert.Equal(RetailHide140, Convert.ToHexString(detail.AsSpan(4 + 140, 2)).ToLowerInvariant());

        using var rec = new PacketWriter();
        PlayerSpawnService.WriteRecord(rec, p, c, TestEmblems.Create(), Array.Empty<InventoryItem>());
        Assert.Equal(RetailHide140, Convert.ToHexString(rec.GetWrittenSpan().Slice(67, 2)).ToLowerInvariant());

        
        
        var view = ActorStatePackets.BuildOnPCViewData2315(p, c, Array.Empty<InventoryItem>(), TestEmblems.Create());
        int recStart = 1 + view[0] + 12;
        Assert.Equal(RetailHide140, Convert.ToHexString(view.AsSpan(recStart + 67, 2)).ToLowerInvariant());

        
        var fresh = new Character { Id = 0x7FB0_2316, Name = "New", Level = 1, Gender = 1, Race = 1, Job = 1 };
        p.Character = fresh;
        using var rec2 = new PacketWriter();
        PlayerSpawnService.WriteRecord(rec2, p, fresh, TestEmblems.Create(), Array.Empty<InventoryItem>());
        Assert.Equal(Character.DefaultEquipHideFlag, BinaryPrimitives.ReadUInt16LittleEndian(rec2.GetWrittenSpan().Slice(67, 2)));
    }

    [Fact]
    public void Detail2310_StatBlock_RepeatsTheVitals()
    {
        
        
        var c = new Character { Id = 0x7FB0_2311, Name = "Stat" };
        var body = Build2310(c, (488u, (ushort)120, 600u, (ushort)150), null);
        var sb = body.AsSpan(body.Length - 60, 28).ToArray();
        uint U(int i) => BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4 * i));
        Assert.Equal(new uint[] { 28, 0x0137B811u, 0x0137B811u, 488, 120, 600, 150 }, Enumerable.Range(0, 7).Select(U).ToArray());
        Assert.Equal(488u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4 + 81)));
        Assert.Equal(120u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4 + 85)));
    }

    [Fact]
    public void VitalsOf_ReadsTheBurst2498()
    {
        var sheet = TraitPackets.BuildPcTrait2498(6698, 4718, 6000, 4000);
        Assert.Equal((6000u, (ushort)4000, 6698u, (ushort)4718), WorldEntryService.VitalsOf(sheet));
        Assert.Null(WorldEntryService.VitalsOf(new byte[10]));
    }

    
    [Fact]
    public void TitleOf_IsTheFirstNonZeroRewardTitle()
    {
        Assert.Equal(206u, KharaTitleStore.TitleOf(572, Khara.Value));   
        Assert.Equal(234u, KharaTitleStore.TitleOf(616, Khara.Value));   
        Assert.Equal(237u, KharaTitleStore.TitleOf(619, Khara.Value));   
        Assert.Equal(155u, KharaTitleStore.TitleOf(410, Khara.Value));   
        Assert.Equal(0u, KharaTitleStore.TitleOf(0, Khara.Value));
    }

    [Fact]
    public void Head2310_CarriesThePublishedTitles_AsRetail()
    {
        
        
        const uint charId = 0x7FB0_2592;
        var selected = new DateTime(2026, 7, 8, 21, 2, 22, 790, DateTimeKind.Utc);
        try
        {
            KharaTitleStore.Publish(charId, new KharaTitleState(0x23C, 0x268, selected, selected), Khara.Value);
            using var w = new PacketWriter();
            KharaTitleStore.WriteHeadTitleA(w, charId, new DateTime(2026, 7, 8, 21, 6, 40, 375, DateTimeKind.Utc));
            Assert.Equal("00000000ce00000000000000000000000000", Convert.ToHexString(w.ToArray()).ToLowerInvariant());
            Assert.Equal(234u, KharaTitleStore.PublishedFor(charId)?.TitleB);

            
            using var running = new PacketWriter();
            KharaTitleStore.WriteHeadTitleA(running, charId, selected.AddSeconds(10));
            Assert.Equal(110000u, BinaryPrimitives.ReadUInt32LittleEndian(running.ToArray()));

            
            var c = new Character { Id = charId, Name = "Titled" };
            var body = Build2310(c, null, null);
            Assert.Equal(206u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4 + 147)));
            Assert.Equal(234u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4 + 185)));
        }
        finally
        {
            KharaTitleStore.Publish(charId, default, Khara.Value);
        }
        Assert.Null(KharaTitleStore.PublishedFor(charId));
    }

    [Fact]
    public void PcRecord_TitleBits_AreWrittenInBitOrder()
    {
        
        
        const uint charId = 0x7FB0_2504;
        var character = new Character { Id = charId, Name = "Ttl", Level = 50, Gender = 1, Race = 1, Job = 1 };
        var player = new PlayerState { EntityId = 0x07863B11u, CharacterId = charId, CharacterUid = 1, Name = "Ttl", Character = character };
        try
        {
            KharaTitleStore.Publish(charId, new KharaTitleState(0x23C, 0x268, null, null), Khara.Value);
            using var w = new PacketWriter();
            PlayerSpawnService.WriteBaseBlock(w, player, character, TestEmblems.Create(), mountId: null);
            var rec = w.ToArray();
            int mask = 70 + 1 + 2 * 3 + 4;   
            Assert.Equal((ushort)(0x0200 | 0x0008 | 0x0010), BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(mask)));
            Assert.Equal(206u, BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(mask + 2)));
            Assert.Equal(234u, BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(mask + 6)));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(mask + 10)));   
        }
        finally
        {
            KharaTitleStore.Publish(charId, default, Khara.Value);
        }
    }

    
    [Fact]
    public void ItemRecord_AbilityValue_IsTheInstancesWhenSet()
    {
        
        
        var bought = new InventoryItem { Id = 0x0E043DA812850D5C, ItemId = 13472531, Count = 1, AbilityValue = 0 };
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(WorldEntryService.BuildItemRecord126(bought).AsSpan(69)));
        var rolled = new InventoryItem { Id = 1, ItemId = 13472531, Count = 1, AbilityValue = 1417 };
        Assert.Equal(1417u, BinaryPrimitives.ReadUInt32LittleEndian(WorldEntryService.BuildItemRecord126(rolled).AsSpan(69)));
        Assert.Equal(1417u, WorldEntryService.RecordAbilityValue(13472531, 1417));
    }

    [Fact]
    public void AbilityValue_SurvivesTheCopyAndTheMail()
    {
        var src = new InventoryItem { ItemId = 33000712, AbilityValue = 0 };
        Assert.Equal(0u, new InventoryItem().CopyInstanceStateFrom(src).AbilityValue);
        Assert.Null(new InventoryItem().CopyInstanceStateFrom(new InventoryItem()).AbilityValue);

        var mail = new MailMessage { AttachedItemId = 33000712, AttachedItemCount = 1, AttachedItemAbilityValue = 0 };
        Assert.Equal(0u, MailService.AttachmentAsBagRow(mail, 7, 99, 3).AbilityValue);
    }

    
    
    
    private const string Retail2723 =
        "00004a000000120029d5010055d60100b9d601001dd7010099fc0100a90d0300bb3403001f35030083350300e735030047360300c95b0300" +
        "2d5c0300915c0300fa5c03005e5d0300c25d0300265e0300";

    private static readonly string[] RetailBurst =
    [
        "011c1c00b3e30000000004000000f5410600bd420600b144060001093d00",
        "01202000b3e30000000005000000f5410600bd420600b144060001093d00fb0a3d00",
        "01242400b3e30000000006000000f5410600bd420600b144060001093d00fb0a3d005f0b3d00",
        "01282800b3e30000000007000000f5410600bd420600b144060001093d00fb0a3d005f0b3d00c30b3d00",
        "012c2c00b3e30000000008000000f5410600bd420600b144060001093d00fb0a3d005f0b3d00c30b3d00270c3d00",
        "01303000b3e30000000009000000f5410600bd420600b144060001093d0067093d00fb0a3d005f0b3d00c30b3d00270c3d00",
        "01343400b3e3000000000a000000f5410600bd420600b144060001093d0067093d00cd093d00fb0a3d005f0b3d00c30b3d00270c3d00",
        "01383800b3e3000000000b000000f5410600bd420600b144060001093d0067093d00cd093d00950a3d00fb0a3d005f0b3d00c30b3d00270c3d00",
        "013c3c00b3e3000000000c000000f5410600bd420600b144060001093d0067093d00cd093d00950a3d00fb0a3d005f0b3d00c30b3d00270c3d00" +
            "8e0c3d00",
        "01404000b3e3000000000d000000f5410600bd420600b144060001093d0067093d00cd093d00950a3d00fb0a3d005f0b3d00c30b3d00270c3d00" +
            "8e0c3d00f20c3d00",
        "01444400b3e3000000000e000000f5410600bd420600b144060001093d0067093d00cd093d00950a3d00fb0a3d005f0b3d00c30b3d00270c3d00" +
            "8e0c3d00f20c3d00560d3d00",
        "01484800b3e3000000000f000000f5410600bd420600b144060001093d0067093d00cd093d00950a3d00fb0a3d005f0b3d00c30b3d00270c3d00" +
            "8e0c3d00f20c3d00560d3d00ba0d3d00",
    ];

    private static uint[] GuildLines2723()
    {
        var b = Convert.FromHexString(Retail2723);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6));
        return Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8 + 4 * i))).ToArray();
    }

    private static readonly Dictionary<uint, byte> OwnThree = new() { [4101] = 1, [4103] = 1, [4108] = 1 };

    [Fact]
    public void GuildLinkBurst_MatchesTheRetailBurst_ByteForByte()
    {
        var burst = UseSkillListLedger.PlanGuildLinkBurst(OwnThree, new Dictionary<uint, byte>(OwnThree), GuildLines2723(),
            GuildTable.Value);

        Assert.Equal(RetailBurst.Length, burst.Count);
        for (int i = 0; i < burst.Count; i++)
            Assert.Equal(RetailBurst[i], Convert.ToHexString(WorldEntryService.BuildUseSkillList2488(burst[i])).ToLowerInvariant());
    }

    [Fact]
    public void GuildLinkBurst_NoneWhenTheLinksAreHeld_OrNothingWasSent()
    {
        var lines = GuildLines2723();
        
        
        var login = GuildSkillBookLearn.SkillListWithGuildLinks(OwnThree, lines, GuildTable.Value);
        var held = login.ToDictionary(s => s.Group, s => s.Level);
        Assert.Empty(UseSkillListLedger.PlanGuildLinkBurst(OwnThree, held, lines, GuildTable.Value));
        Assert.Empty(UseSkillListLedger.PlanGuildLinkBurst(OwnThree, null, lines, GuildTable.Value));
        
        var burst = UseSkillListLedger.PlanGuildLinkBurst(OwnThree, new Dictionary<uint, byte>(OwnThree), lines, GuildTable.Value);
        Assert.Equal(WorldEntryService.BuildUseSkillList2488(login), WorldEntryService.BuildUseSkillList2488(burst[^1]));
    }
}
