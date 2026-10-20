using System.Buffers.Binary;
using System.Text.Json;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// P3 unit A2: 1547/2792 card-book drag take-off, 1512/2722 guild skill learn, 1701/3038 outfit release.
/// The 1547, 2792, 1701 and 3038 tests pin the layout that the client's builder or reader defines and are not
/// verified against retail traffic. The 1512/2722 tests use two live retail frames.
/// </summary>
public class UnitA2P3Batch1Tests
{
    
    [Fact]
    public void CardBookTakeOff1547_ReadsTheBuilderBlob9()
    {
        
        
        
        var body = Convert.FromHexString("0416ec6f5401030a01");
        Assert.True(CardBookTakeOffHandler.TryParseRequest(body, out var req));
        Assert.Equal(new CardBookTakeOffHandler.TakeOffRequest(4, 22, 22310892, 3, 10, 1), req);
        Assert.Equal(20 + 2 * 35 + 22, CardBookTakeOffHandler.BagSlotToFlat(req.Bag, req.Slot));

        
        Assert.True(CardBookTakeOffHandler.TryParseRequest(Convert.FromHexString("0132ec6f5401000002"), out var dflt));
        Assert.Equal((byte)1, dflt.Amount);
        Assert.Equal((byte)1, dflt.Kind);
        Assert.Equal(0, CardBookTakeOffHandler.BagSlotToFlat(1, 50));
        Assert.Equal(-1, CardBookTakeOffHandler.BagSlotToFlat(1, 49));
        Assert.False(CardBookTakeOffHandler.TryParseRequest(Convert.FromHexString("0132ec6f54010000"), out _));
    }

    private const uint Owner = 7;
    private const uint Card = 22310892;
    private static readonly ItemData CardTemplate = new() { ID = Card, Item_Type = 13, Stack_Max = 20, Bind_Type = 1 };
    private static readonly IReadOnlyDictionary<uint, ItemData> NoBags = new Dictionary<uint, ItemData>();

    private static InventoryItem Row(short slot, uint itemId = Card, int count = 1, byte refine = 0, bool bound = false) => new()
    {
        Id = 0x0E043DA80DB00100UL + (ulong)slot, CharacterId = Owner, ItemId = itemId, Count = count, Slot = slot,
        Refine = refine, Bound = bound,
    };

    [Fact]
    public void CardBookTakeOff1547_PlacesOnTheDroppedCell()
    {
        var owned = new List<InventoryItem> { Row(0, count: 5), Row(1, itemId: 11100001) };

        
        var free = CardBookTakeOffHandler.ChoosePlacement(owned, NoBags, CardTemplate, Card, 0, false, 3, 5);
        Assert.Equal(new CardBookTakeOffHandler.Placement(null, 5), free);

        
        var onStack = CardBookTakeOffHandler.ChoosePlacement(owned, NoBags, CardTemplate, Card, 0, false, 3, 0);
        Assert.Same(owned[0], onStack!.Value.Join);

        
        var onOther = CardBookTakeOffHandler.ChoosePlacement(owned, NoBags, CardTemplate, Card, 0, false, 3, 1);
        Assert.Same(owned[0], onOther!.Value.Join);

        
        var otherGrade = CardBookTakeOffHandler.ChoosePlacement(owned, NoBags, CardTemplate, Card, 10, false, 1, 0);
        Assert.Equal(new CardBookTakeOffHandler.Placement(null, 2), otherGrade);
        var bound = CardBookTakeOffHandler.ChoosePlacement(owned, NoBags, CardTemplate, Card, 0, true, 1, 0);
        Assert.Equal(new CardBookTakeOffHandler.Placement(null, 2), bound);

        
        var full = CardBookTakeOffHandler.ChoosePlacement(owned, NoBags, CardTemplate, Card, 0, false, 16, 0);
        Assert.Equal(new CardBookTakeOffHandler.Placement(null, 2), full);
        
        Assert.Null(CardBookTakeOffHandler.ChoosePlacement(owned, NoBags, CardTemplate, Card, 0, false, 21, 5));
    }

    [Fact]
    public void CardBookTakeOff1547_BagFullIsNoPlacement()
    {
        var owned = Enumerable.Range(0, 20).Select(s => Row((short)s, itemId: 11100001)).ToList();
        Assert.Null(CardBookTakeOffHandler.ChoosePlacement(owned, NoBags, CardTemplate, Card, 0, false, 1, 3));
        
        Assert.Null(CardBookTakeOffHandler.ChoosePlacement(owned, NoBags, CardTemplate, Card, 0, false, 1,
            CardBookTakeOffHandler.BagSlotToFlat(2, 0)));
    }

    [Fact]
    public void CardBookTakeOff2792_CodesAreThe2801Ones()
    {
        
        Assert.Equal((ushort)22876, CardBookTakeOffHandler.ResultNotInBook);
        Assert.Equal((ushort)20020, CardBookTakeOffHandler.ResultBagFull);
    }

    
    [Fact]
    public void GuildSkillLearn1512_LiveRetailFramesParse()
    {
        
        Assert.True(GuildSkillLearnPackets.TryParseRequest(Convert.FromHexString(
            "360000000d0025d5010026d5010027d5010028d5010029d50100b5d6010099fc010019d7010051d6010052d6010053d6010054d6010055d60100"),
            out var first));
        Assert.Equal(new uint[] { 120101, 120102, 120103, 120104, 120105, 120501, 130201, 120601, 120401, 120402, 120403, 120404, 120405 }, first);
        Assert.True(GuildSkillLearnPackets.TryParseRequest(Convert.FromHexString(
            "1e0000000700b5d6010019d7010051d6010052d6010053d6010054d6010055d60100"), out var second));
        Assert.Equal(7, second.Length);
    }

    [Fact]
    public void GuildSkillLearn2722_NotMasterIsRetails19()
    {
        
        var guild = new Guild { Id = 1, MasterCharId = 100 };
        Assert.Equal((ushort)19, GuildSkillLearnPackets.GateResult(guild, 200));
        Assert.Equal("1300", Hex(BitConverter.GetBytes(GuildSkillLearnPackets.GateResult(guild, 200))));
        Assert.Equal((ushort)0, GuildSkillLearnPackets.GateResult(guild, 100));
        Assert.Equal((ushort)7, GuildSkillLearnPackets.GateResult(null, 100));
    }

    [Fact]
    public void GuildSkillLearn2722_RefusalCodesFromTheGuildResultSwitch()
    {
        
        
        var table = GuildSkillLearnPackets.Index(Load<GuildSkillLevel>("Guild_Skill_Level.json"));
        GuildSkillLearnPackets.LearnRefusal r;

        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 120101, 120102, 120103, 120104, 120105 }, Array.Empty<uint>(), 5, table, out r));
        Assert.Equal((ushort)47, GuildSkillLearnPackets.ResultCodeOf(r));
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 120502 }, Array.Empty<uint>(), 20, table, out r));
        Assert.Equal((ushort)48, GuildSkillLearnPackets.ResultCodeOf(r));
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 120501, 120501 }, Array.Empty<uint>(), 20, table, out r));
        Assert.Equal((ushort)48, GuildSkillLearnPackets.ResultCodeOf(r));
        
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 120501, 120601, 120401 }, Array.Empty<uint>(), 19, table, out r));
        Assert.Equal((ushort)48, GuildSkillLearnPackets.ResultCodeOf(r));
        
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 120601 }, Array.Empty<uint>(), 20, table, out r));
        Assert.Equal((ushort)49, GuildSkillLearnPackets.ResultCodeOf(r));
        
        Assert.NotNull(GuildSkillLearnPackets.PlanLearn(new uint[] { 120101, 120102, 120103, 120104, 120105, 120501, 130201, 120601, 120401, 120402, 120403, 120404, 120405 },
            Array.Empty<uint>(), 20, table, out r));
        Assert.Equal((ushort)0, GuildSkillLearnPackets.ResultCodeOf(r));

        
        var synthetic = GuildSkillLearnPackets.Index(new List<GuildSkillLevel>
        {
            new() { Guild_Skill_ID = 990101, Need_GuildLevel = 1, Need_Skill_ID_2 = 1 },
        });
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 990101 }, Array.Empty<uint>(), 20, synthetic, out r));
        Assert.Equal((ushort)50, GuildSkillLearnPackets.ResultCodeOf(r));
    }

    
    [Fact]
    public void OutfitDivideRelease1701_ReadsTwoUids()
    {
        
        
        var body = Convert.FromHexString("84cc1be0a73d040e2c888b12a83d040e");
        Assert.True(OutfitDivideReleaseItemHandler.TryParseRequest(body, out var target, out var scroll));
        Assert.Equal(0x0E043DA7E01BCC84UL, target);
        Assert.Equal(0x0E043DA8128B882CUL, scroll);
        Assert.False(OutfitDivideReleaseItemHandler.TryParseRequest(body.AsSpan(0, 15), out _, out _));
    }

    [Fact]
    public void OutfitDivideRelease3038_IsTheReadersBlob136()
    {
        
        
        var item = new InventoryItem { Id = 0x0E043DA7E01BCC84UL, CharacterId = Owner, ItemId = 11304311, StorageType = 1, Slot = 3 };
        var record = WorldEntryService.BuildItemRecord126(item);
        var body = OutfitDivideReleaseItemHandler.BuildAnswer3038(0, 0x0E043DA8128B882CUL, record);
        Assert.Equal(136, body.Length);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.Equal(0x0E043DA8128B882CUL, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(2)));
        Assert.Equal(item.Id, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(10)));
        Assert.Equal(record, body.AsSpan(10).ToArray());
        
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(37)) & 0x10);
    }

    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static List<T> Load<T>(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", file)))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + file);
        return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(Path.Combine(dir.FullName, "data", file)),
            DataJsonOptions)!;
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
