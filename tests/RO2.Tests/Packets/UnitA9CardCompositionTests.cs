using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// 1456 ReqCardComposition (<see cref="CardCompositionHandler"/>): the plan and its refusal codes, where the result
/// goes (<see cref="CardStacks"/>), and the emptied-cell 2379 record (<see cref="EmptiedItemRecord"/>).
/// </summary>
public class UnitA9CardCompositionTests
{
    private const uint Owner = 7;
    private const uint Handle = 0x070F3C11;       
    private const uint Stone = 22300102;           
    private const uint Ticket = 50001101;          
    private const uint Source = 22310831;          
    private const uint Result = 22310871;
    private const uint CardA = 22310793, CardB = 22310792, CardC = 22310294, CardD = 22310289;   
    private const uint Grade4Card = 22310832;

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

    private static async Task<GameDataStore> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-cardcompose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":22300102,"Item_Type":21,"Stack_Max":255,"Bind_Type":0,"Durability":0},
                 {"ID":50001101,"Item_Type":40,"Stack_Max":255,"Bind_Type":0,"Durability":0},
                 {"ID":22310831,"Item_Type":13,"Stack_Max":20,"Bind_Type":1,"Durability":10000},
                 {"ID":22310871,"Item_Type":13,"Stack_Max":20,"Bind_Type":1,"Durability":10000},
                 {"ID":22310793,"Item_Type":13,"Stack_Max":20,"Bind_Type":1,"Durability":10000},
                 {"ID":22310792,"Item_Type":13,"Stack_Max":20,"Bind_Type":1,"Durability":10000},
                 {"ID":22310294,"Item_Type":13,"Stack_Max":20,"Bind_Type":0,"Durability":10000},
                 {"ID":22310289,"Item_Type":13,"Stack_Max":20,"Bind_Type":0,"Durability":10000},
                 {"ID":22310832,"Item_Type":13,"Stack_Max":20,"Bind_Type":1,"Durability":10000}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "CardInfo.json"), """
                [{"Card_ID":22310831,"Grade_Type":3,"Compose_Result":22310871},
                 {"Card_ID":22310871,"Grade_Type":3,"Compose_Result":0},
                 {"Card_ID":22310793,"Grade_Type":3,"Compose_Result":22310807},
                 {"Card_ID":22310792,"Grade_Type":3,"Compose_Result":22310806},
                 {"Card_ID":22310294,"Grade_Type":3,"Compose_Result":22310714},
                 {"Card_ID":22310289,"Grade_Type":3,"Compose_Result":22310709},
                 {"Card_ID":22310832,"Grade_Type":4,"Compose_Result":22310872}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "CardCompose.json"), """
                [{"ItemID":22300102,"Card_Grade":3,"Compose_Ratio":5000,"Special_Compose_Ratio":500}]
                """);
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] Hex(string s) => Convert.FromHexString(s);
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static InventoryItem Row(ulong uid, uint itemId, int count, short flat, short durability) =>
        new() { Id = uid, CharacterId = Owner, ItemId = itemId, Count = count, Slot = flat, StorageType = 0, Durability = durability };

    
    
    private const string ArchiveRequest =
        "56000000133b11000002000401bdb98c0da83d040e01030949eb6800a83d040e01050002156ab98c0da83d040e01" +
        "030576b98c0da83d040e01030685b98c0da83d040e010307a5b98c0da83d040e01030895b98c0da83d040e01";

 /// <summary>
 /// Rebuilds the bag before the request from the answer: each 2379 carries a stack's uid, template, cell and the count
 /// it keeps (one less than before), and the 2333 carries the result stack. Flat slot indices: bag 2 slot k = 20 + k,
 /// bag 3 slot k = 55 + k, bag 4 slot k = 90 + k.
 /// </summary>
    private static List<InventoryItem> ArchiveBag() => new()
    {
        Row(0x0E043DA80D8CB9BDUL, Stone, 239, 91, 0),       
        Row(0x0E043DA80068EB49UL, Ticket, 95, 64, 0),       
        Row(0x0E043DA80D8CB96AUL, Source, 2, 41, 10000),    
        Row(0x0E043DA80D8CB976UL, CardA, 1, 60, 10000),     
        Row(0x0E043DA80D8CB985UL, CardB, 1, 61, 10000),     
        Row(0x0E043DA80D8CB9A5UL, CardC, 1, 62, 10000),     
        Row(0x0E043DA80D8CB995UL, CardD, 1, 63, 10000),     
        Row(0x0E043DA80D8CB9C0UL, Result, 10, 65, 10000),   
    };

    [Fact]
    public void Request_ParsesTheArchiveFrame()
    {
        var req = CardCompositionHandler.ParseRequest(Hex(ArchiveRequest));
        Assert.NotNull(req);
        Assert.Equal((byte)0, req.SourceIndex);
        Assert.Equal(2, req.Catalysts.Count);
        Assert.Equal(new CardCompositionHandler.Descriptor(4, 1, 0x0E043DA80D8CB9BDUL, 1), req.Catalysts[0]);
        Assert.Equal(5, req.Cards.Count);
        Assert.Equal(new CardCompositionHandler.Descriptor(2, 21, 0x0E043DA80D8CB96AUL, 1), req.Cards[0]);
    }

    [Fact]
    public async Task ArchiveRequest_IsRolled_OnTheStoneRatio_InRetailOrder()
    {
        var store = await Store.Value;
        var req = CardCompositionHandler.ParseRequest(Hex(ArchiveRequest))!;
        var (plan, refusal) = CardCompositionHandler.PlanFor(req, ArchiveBag(), store);

        Assert.NotNull(plan);
        Assert.Equal((ushort)0, refusal);
        Assert.Equal(5000, plan.Ratio);
        Assert.Equal(Result, plan.ResultCardId);
        Assert.Equal(0x0E043DA80D8CB96AUL, plan.Source.Id);
        
        Assert.Equal(new ulong[] { 0x0E043DA80D8CB9BD, 0x0E043DA80068EB49, 0x0E043DA80D8CB976, 0x0E043DA80D8CB985,
                                   0x0E043DA80D8CB9A5, 0x0E043DA80D8CB995 },
            plan.Materials.Select(m => m.Id).ToArray());
        
        Assert.Equal("0000af6f5401d76f5401", Hex(CardPackets.BuildCardComposition2616(0, plan.Source.ItemId, plan.ResultCardId)));
    }

    [Fact]
    public async Task EmptiedStacks_CarryOnlyUidTemplateAndCell_AsRetail()
    {
        
        
        var retail = new Dictionary<ulong, string>
        {
            [0x0E043DA80D8CB976] = "76b98c0da83d040e896f54010000000305",
            [0x0E043DA80D8CB985] = "85b98c0da83d040e886f54010000000306",
            [0x0E043DA80D8CB9A5] = "a5b98c0da83d040e966d54010000000307",
            [0x0E043DA80D8CB995] = "95b98c0da83d040e916d54010000000308",
        };
        await Store.Value;
        foreach (var row in ArchiveBag().Where(r => retail.ContainsKey(r.Id)))
        {
            row.Count = 0;
            row.Refine = 3;
            row.Bound = true;
            var body = QuestProgressPackets.BuildItemRemoved2379(Handle, EmptiedItemRecord.Build(row));
            var expected = "028a008a00c7e2113c0f0700000000" + retail[row.Id] + new string('0', 2 * (126 - 17));
            Assert.Equal(expected, Hex(body));
        }
    }

    [Fact]
    public async Task StacksThatKeepACount_CarryTheirFullRecord_AsRetail()
    {
        await Store.Value;
        var bag = ArchiveBag();
        
        var stone = bag[0]; stone.Count = 238;
        var source = bag[2]; source.Count = 1;
        Assert.Equal("028a008a00c7e2113c0f0700000000bdb98c0da83d040ec64554010000ee0401" + new string('0', 2 * (126 - 17)),
            Hex(QuestProgressPackets.BuildItemRemoved2379(Handle, ItemPackets.BuildItemRecord126(stone))));
        Assert.Equal("028a008a00c7e2113c0f07000000006ab98c0da83d040eaf6f5401102701021500" + new string('0', 2 * (126 - 18)),
            Hex(QuestProgressPackets.BuildItemRemoved2379(Handle, ItemPackets.BuildItemRecord126(source))));
    }

    [Fact]
    public async Task Result_JoinsOnlyAPlusZeroUnboundStack_AsRetail()
    {
        var store = await Store.Value;
        var bag = ArchiveBag();
        
        bag.Add(new InventoryItem { Id = 900, CharacterId = Owner, ItemId = Result, Count = 3, Slot = 5, Refine = 10 });
        bag.Add(new InventoryItem { Id = 901, CharacterId = Owner, ItemId = Result, Count = 3, Slot = 6, Bound = true });

        var join = CardStacks.FindJoinStack(bag, store.GetItem(Result), Result, grade: 0, bound: false);
        Assert.NotNull(join);
        Assert.Equal(0x0E043DA80D8CB9C0UL, join.Id);

        
        join.Count += 1;
        Assert.Equal("00000000c0b98c0da83d040ed76f540110270b030a" + new string('0', 2 * (126 - 17)),
            Hex(LootNotifyPackets.BuildPickupItem2333(ItemPackets.BuildItemRecord126(join))));
    }

    [Fact]
    public async Task Result_StartsANewStack_WhenOnlyEnforcedOrBoundOnesExist()
    {
        var store = await Store.Value;
        var bag = new List<InventoryItem>
        {
            new() { Id = 900, CharacterId = Owner, ItemId = Result, Count = 3, Slot = 5, Refine = 10 },
            new() { Id = 901, CharacterId = Owner, ItemId = Result, Count = 3, Slot = 6, Bound = true },
            new() { Id = 902, CharacterId = Owner, ItemId = Result, Count = 20, Slot = 7 },   
        };
        Assert.Null(CardStacks.FindJoinStack(bag, store.GetItem(Result), Result, grade: 0, bound: false));
    }

 /// <summary>
 /// A refusal whose descriptors include quantity 0 (cells (3,5) and (3,6)) is answered with
 /// 2616 <c>2d5e0000000000000000</c> (24109).
 /// </summary>
    [Fact]
    public async Task QtyZeroDescriptor_Is24109()
    {
        var store = await Store.Value;
        var body = Hex("56000000133b110000020003022cd5e90da83d040e010301dcd8e7f9a73d040e010500030414beef0da83d040e01" +
                       "030b4b20ee0da83d040e01030a1522ee0da83d040e01030565beef0da83d040e000306dc21ee0da83d040e00");
        var bag = new List<InventoryItem>
        {
            Row(0x0E043DA80DE9D52CUL, Stone, 10, 57, 0),
            Row(0x0E043DA7F9E7D8DCUL, Ticket, 10, 56, 0),
            Row(0x0E043DA80DEFBE14UL, Source, 1, 59, 10000),
            Row(0x0E043DA80DEE204BUL, CardA, 1, 66, 10000),
            Row(0x0E043DA80DEE2215UL, CardB, 1, 65, 10000),
            Row(0x0E043DA80DEFBE65UL, CardC, 1, 60, 10000),
            Row(0x0E043DA80DEE21DCUL, CardD, 1, 61, 10000),
        };
        var (plan, refusal) = CardCompositionHandler.PlanFor(CardCompositionHandler.ParseRequest(body)!, bag, store);
        Assert.Null(plan);
        Assert.Equal((ushort)24109, refusal);
        Assert.Equal("2d5e0000000000000000", Hex(CardPackets.BuildCardComposition2616(refusal, 0, 0)));

        
        body[^1] = 1; body[^12] = 1;
        Assert.NotNull(CardCompositionHandler.PlanFor(CardCompositionHandler.ParseRequest(body)!, bag, store).Plan);
    }

 /// <summary>
 /// A refusal whose request names the same cell twice ((1,53) in both vector lists, same uid) is answered with
 /// 2616 <c>2e5e0000000000000000</c> (24110).
 /// </summary>
    [Fact]
    public async Task DuplicateUid_Is24110()
    {
        var store = await Store.Value;
        var body = Hex("56000000133a1100000200013511c8d90ea83d040e0101384c78e6fea73d040e010500013769c3cbf6a73d040e01" +
                       "0132ad00d80ea83d040e010133ae00d80ea83d040e0101340fc8d90ea83d040e01013511c8d90ea83d040e02");
        var bag = new List<InventoryItem>
        {
            Row(0x0E043DA80ED9C811UL, Stone, 2, 3, 0),
            Row(0x0E043DA7FEE6784CUL, Ticket, 10, 6, 0),
            Row(0x0E043DA7F6CBC369UL, Source, 1, 5, 10000),
            Row(0x0E043DA80ED800ADUL, CardA, 1, 0, 10000),
            Row(0x0E043DA80ED800AEUL, CardB, 1, 1, 10000),
            Row(0x0E043DA80ED9C80FUL, CardC, 1, 2, 10000),
        };
        var (plan, refusal) = CardCompositionHandler.PlanFor(CardCompositionHandler.ParseRequest(body)!, bag, store);
        Assert.Null(plan);
        Assert.Equal((ushort)24110, refusal);
        Assert.Equal("2e5e0000000000000000", Hex(CardPackets.BuildCardComposition2616(refusal, 0, 0)));
    }

    [Fact]
    public async Task OtherRefusals_FollowTheClientStrings()
    {
        var store = await Store.Value;
        var req = CardCompositionHandler.ParseRequest(Hex(ArchiveRequest))!;

        
        var missing = ArchiveBag().Where(r => r.ItemId != CardD).ToList();
        Assert.Equal((ushort)24109, CardCompositionHandler.PlanFor(req, missing, store).Refusal);
        var banked = ArchiveBag(); banked[6].StorageType = 2;
        Assert.Equal((ushort)24109, CardCompositionHandler.PlanFor(req, banked, store).Refusal);

        
        var mixed = ArchiveBag(); mixed[6].ItemId = Grade4Card;
        Assert.Equal((ushort)24110, CardCompositionHandler.PlanFor(req, mixed, store).Refusal);

        
        var noStone = ArchiveBag(); noStone[0].ItemId = Ticket;
        Assert.Equal((ushort)24110, CardCompositionHandler.PlanFor(req, noStone, store).Refusal);

        
        var four = new CardCompositionHandler.Request(req.SourceIndex, req.Catalysts, req.Cards.Take(4).ToList());
        Assert.Equal((ushort)24110, CardCompositionHandler.PlanFor(four, ArchiveBag(), store).Refusal);

        
        var noResult = ArchiveBag(); noResult[2].ItemId = Result;
        Assert.Equal((ushort)24110, CardCompositionHandler.PlanFor(req, noResult, store).Refusal);
    }

    [Fact]
    public async Task StoneOnly_Request_IsRolled()
    {
        
        
        var store = await Store.Value;
        var req = CardCompositionHandler.ParseRequest(Hex(ArchiveRequest))!;
        var stoneOnly = new CardCompositionHandler.Request(req.SourceIndex, req.Catalysts.Take(1).ToList(), req.Cards);
        var (plan, _) = CardCompositionHandler.PlanFor(stoneOnly, ArchiveBag(), store);
        Assert.NotNull(plan);
        Assert.Equal(5, plan.Materials.Count);
    }
}
