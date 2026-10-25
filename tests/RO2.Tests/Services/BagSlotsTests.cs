using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Inventory.Domain;
using RO2.Server.Zone.Services;
using RO2.Tests.Economy;
using Xunit;
using MongoInventoryRepository = RO2.Inventory.Infrastructure.Mongo.InventoryRepository;
using PostgresInventoryRepository = RO2.Inventory.Infrastructure.Postgres.InventoryRepository;

namespace RO2.Tests.Services;

/// <summary>
/// Characterization of the bag slot math through its public entry points (<see cref="BagCapacity.FreeCells"/>, both
/// providers' <c>LowestFreeSlot</c>, <c>InventoryStacking.AddAsync</c> and <see cref="BagStackPlanner.Plan"/>).
/// The expected values were recorded against the code before the slot math moved into RO2.Inventory.Domain and must
/// not change with the move: a different cell is client-visible and unique-index-visible.
/// </summary>
public class BagSlotsTests
{
    private const uint Bag30 = 60000366;
    private const uint Bag10 = 60000100;
    private const uint Sword = 500;

    private static InventoryItem Row(ulong id, short slot, byte storage = 0, uint itemId = Sword, int count = 1)
        => new() { Id = id, CharacterId = 7, ItemId = itemId, Count = count, Slot = slot, StorageType = storage };

    private static readonly Dictionary<uint, ItemData> Bags = new()
    {
        [Bag30] = new ItemData { ID = Bag30, Bag_Size = 30 },
        [Bag10] = new ItemData { ID = Bag10, Bag_Size = 10 },
        [1] = new ItemData { ID = 1, Bag_Size = 99 },    // clamped to the 35 stride
        [2] = new ItemData { ID = 2, Bag_Size = -5 },    // clamped to 0
    };

    private static short[] Free(IEnumerable<InventoryItem> rows) => BagCapacity.FreeCells(rows, Bags).ToArray();

    private static short[] Cells(IEnumerable<int> flats) => flats.Select(f => (short)f).ToArray();

    // --- FreeCells ---

    [Fact]
    public void FreeCells_EmptyMainBag_IsCells0To19InOrder()
        => Assert.Equal(Cells(Enumerable.Range(0, 20)), Free([]));

    [Fact]
    public void FreeCells_PartiallyFullMainBag_ListsTheGapsLowestFirst()
    {
        short[] taken = [0, 1, 4, 5, 6, 12, 19];
        var rows = taken.Select((s, i) => Row((ulong)(i + 1), s)).ToList();
        Assert.Equal(new short[] { 2, 3, 7, 8, 9, 10, 11, 13, 14, 15, 16, 17, 18 }, Free(rows));
    }

    [Fact]
    public void FreeCells_FullMainBag_IsEmpty()
        => Assert.Empty(Free(Enumerable.Range(0, 20).Select(s => Row((ulong)(s + 1), (short)s))));

    [Fact]
    public void FreeCells_IgnoresWornBankUnplacedAndOutOfBagRows()
    {
        var rows = new List<InventoryItem>
        {
            Row(1, 0, storage: 1), Row(2, 1, storage: 2), Row(3, -1), Row(4, 2, storage: 4),
        };
        Assert.Equal(Cells(Enumerable.Range(0, 20)), Free(rows));
    }

    [Fact]
    public void FreeCells_ExtraBagAtItsStride_FollowsTheMainBagAndSkipsTakenCells()
    {
        // Bag30 in character-bag cell 1: flat 20 + 35*1 + k = 55..84. Main bag full, flat 56 and 60 taken.
        var rows = Enumerable.Range(0, 20).Select(s => Row((ulong)(s + 1), (short)s)).ToList();
        rows.Add(Row(100, 1, storage: 3, itemId: Bag30));
        rows.Add(Row(101, 56));
        rows.Add(Row(102, 60));
        Assert.Equal(Cells(Enumerable.Range(55, 30).Where(f => f != 56 && f != 60)), Free(rows));
    }

    [Fact]
    public void FreeCells_BagsAreWalkedByCharacterBagCell_NotByInsertionOrder()
    {
        // Bag10 in cell 2 listed before Bag30 in cell 0; main bag full.
        var rows = Enumerable.Range(0, 20).Select(s => Row((ulong)(s + 1), (short)s)).ToList();
        rows.Add(Row(100, 2, storage: 3, itemId: Bag10));
        rows.Add(Row(101, 0, storage: 3, itemId: Bag30));
        Assert.Equal(Cells(Enumerable.Range(20, 30).Concat(Enumerable.Range(90, 10))), Free(rows));
    }

    [Fact]
    public void FreeCells_BagSizeIsClampedToTheStride_AndUnknownOrNegativeBagsProvideNothing()
    {
        var oversize = Free([Row(100, 0, storage: 3, itemId: 1)]);
        Assert.Equal(Cells(Enumerable.Range(0, 20).Concat(Enumerable.Range(20, 35))), oversize);   // last cell is flat 54

        var nothing = new List<InventoryItem>
        {
            Row(101, 0, storage: 3, itemId: 2),       // Bag_Size -5
            Row(102, 1, storage: 3, itemId: 9999),    // not in the table
            Row(103, 3, storage: 3, itemId: Bag30),   // character-bag cell 3 does not exist
            Row(104, -1, storage: 3, itemId: Bag30),  // unplaced
        };
        Assert.Equal(Cells(Enumerable.Range(0, 20)), Free(nothing));
    }

    // --- LowestFreeSlot (both providers) ---

    public static TheoryData<int[], short> LowestCases => new()
    {
        { [], 0 },
        { [0, 1, 2], 3 },
        { [0, 2, 3], 1 },
        { [-1, 1], 0 },
        { [-1, 0, 1, 5], 2 },
        { [1, 1, 0, 0, 2], 3 },
        { Enumerable.Range(0, 100).ToArray(), 100 },
    };

    [Theory, MemberData(nameof(LowestCases))]
    public void LowestFreeSlot_Mongo(int[] occupied, short expected)
        => Assert.Equal(expected, MongoInventoryRepository.LowestFreeSlot(occupied));

    [Theory, MemberData(nameof(LowestCases))]
    public void LowestFreeSlot_Postgres(int[] occupied, short expected)
        => Assert.Equal(expected, PostgresInventoryRepository.LowestFreeSlot(occupied));

    // --- stacking merge decision ---

    private static List<(uint Item, int Count, bool Restacked)> Landed(IReadOnlyList<InventoryStacking.Placed> placed)
        => placed.Select(p => (p.Stack.ItemId, p.Stack.Count, p.Restacked)).ToList();

    private static async Task<(FakeInventoryRepository Repo, IReadOnlyList<InventoryStacking.Placed> Placed)> AddAsync(
        byte stackMax, int quantity, params (short Slot, int Count)[] held)
    {
        var repo = new FakeInventoryRepository(new RepoScheduler());
        ulong id = 10;
        foreach (var (slot, count) in held) repo.Seed(Row(id++, slot, itemId: 77, count: count));
        var placed = await InventoryStacking.AddAsync(repo, 7, 77, quantity, stackMax);
        return (repo, placed);
    }

    [Fact]
    public async Task Add_FitsAnExistingStack_MergesIntoTheLowestCellOne()
    {
        // 90 at cell 5 and 95 at cell 2 both take +4 (max 99): the lowest cell wins.
        var (repo, placed) = await AddAsync(99, 4, (5, 90), (2, 95));
        Assert.Equal([(77u, 99, true)], Landed(placed));
        Assert.Equal((short)2, placed[0].Stack.Slot);
        Assert.Equal(2, repo.Rows.Count);
    }

    [Fact]
    public async Task Add_DoesNotFitAnyStack_GoesToNewFullStacksFirst_NeverTopsUpAPartial()
    {
        var (repo, placed) = await AddAsync(10, 25, (0, 8));
        Assert.Equal([(77u, 10, false), (77u, 10, false), (77u, 5, false)], Landed(placed));
        Assert.Equal(new short[] { 1, 2, 3 }, placed.Select(p => p.Stack.Slot).ToArray());
        Assert.Equal(8, repo.Rows.Single(r => r.Slot == 0).Count);
    }

    [Fact]
    public async Task Add_ExactlyFillingAStack_Merges_ButOneOverGoesNew()
    {
        var (_, fills) = await AddAsync(10, 2, (3, 8));
        Assert.Equal([(77u, 10, true)], Landed(fills));
        var (_, over) = await AddAsync(10, 3, (3, 8));
        Assert.Equal([(77u, 3, false)], Landed(over));
    }

    [Fact]
    public async Task Add_NotStackable_IsOneNewStackPerUnit_AndNeverMerges()
    {
        var (_, placed) = await AddAsync(1, 3, (0, 1));
        Assert.Equal([(77u, 1, false), (77u, 1, false), (77u, 1, false)], Landed(placed));
        var (_, zero) = await AddAsync(0, 2);   // Stack_Max 0 behaves as 1
        Assert.Equal([(77u, 1, false), (77u, 1, false)], Landed(zero));
    }

    [Fact]
    public async Task Add_IgnoresEmptyAndNonBagStacks_ForTheMerge()
    {
        var repo = new FakeInventoryRepository(new RepoScheduler());
        repo.Seed(Row(10, 0, itemId: 77, count: 0));                 // emptied stack
        repo.Seed(Row(11, 1, storage: 2, itemId: 77, count: 1));     // bank
        var placed = await InventoryStacking.AddAsync(repo, 7, 77, 1, 10);
        Assert.Equal([(77u, 1, false)], Landed(placed));
    }

    // --- planner (restack vs new cells) ---

    private static BagStack S(ulong id, int count, short slot, uint item = 77) => new(id, item, count, slot);

    [Fact]
    public void Plan_Restacks_IntoTheLowestCellStackThatFitsWhole()
    {
        var plan = BagStackPlanner.Plan([S(1, 9, 8), S(2, 5, 3), S(3, 10, 0)], [4, 5], 77, 3, 10);
        Assert.Equal(2UL, plan!.RestackId);   // cell 3 (count 5) fits; cell 8 (count 9) does not; cell 0 is full
        Assert.Empty(plan.NewStacks);
    }

    [Fact]
    public void Plan_NeedsNewCells_TakesTheFreeCellsInGivenOrder_FullStacksFirst()
    {
        var plan = BagStackPlanner.Plan([S(1, 9, 8)], [4, 5, 20], 77, 25, 10);
        Assert.Null(plan!.RestackId);
        Assert.Equal(new[] { ((short)4, 10), ((short)5, 10), ((short)20, 5) }, plan.NewStacks.ToArray());
    }

    [Fact]
    public void Plan_NotEnoughFreeCells_IsNull()
        => Assert.Null(BagStackPlanner.Plan([], [4, 5], 77, 25, 10));

    [Fact]
    public void Plan_NonStackable_NeverRestacks()
    {
        var plan = BagStackPlanner.Plan([S(1, 1, 0)], [2, 3], 77, 2, 1);
        Assert.Null(plan!.RestackId);
        Assert.Equal(new[] { ((short)2, 1), ((short)3, 1) }, plan.NewStacks.ToArray());
    }
}
