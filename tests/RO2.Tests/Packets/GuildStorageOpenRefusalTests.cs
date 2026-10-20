using System.Buffers.Binary;
using System.Text.Json;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Shared lookups over the repo's data/ folder, found by walking up from the test binaries.</summary>
internal static class UnitA10Data
{
    internal static string DataFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
    }

 /// <summary>The ItemInfo rows the tests need, as ItemData (the fields the code under test reads).</summary>
    internal static async Task<Dictionary<uint, ItemData>> ItemsAsync(Func<JsonElement, bool> keep)
    {
        await using var fs = File.OpenRead(DataFile("ItemInfo.json"));
        using var doc = await JsonDocument.ParseAsync(fs);
        var byId = new Dictionary<uint, ItemData>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (!keep(r)) continue;
            var id = r.GetProperty("ID").GetUInt32();
            byId.TryAdd(id, new ItemData
            {
                ID = id,
                Grade = r.GetProperty("Grade").GetByte(),
                Price_Buy = r.GetProperty("Price_Buy").GetUInt32(),
                Stack_Max = r.GetProperty("Stack_Max").GetByte(),
                Equip_Type = r.GetProperty("Equip_Type").GetUInt16(),
                Durability = r.GetProperty("Durability").GetUInt32(),
            });
        }
        return byId;
    }

    internal static Task<Dictionary<uint, ItemData>> ItemsAsync(params uint[] ids)
    {
        var want = new HashSet<uint>(ids);
        return ItemsAsync(r => want.Contains(r.GetProperty("ID").GetUInt32()));
    }
}

/// <summary>1489 ReqOpenGuildStorage and its 2674 answers, checked byte for byte against reference bodies.</summary>
public class GuildStorageOpenRefusalTests
{
 /// <summary>Refused open, answered with a 2674 refusal.</summary>
    private const string Refused1489 = "d003e843ff6eee4d93467b13c0bd4fddec000050";
    private const string Refused2674 = "37000a00000000000000000000000000";

 /// <summary>Answered open of an empty vault.</summary>
    private const string Answered1489 = "d335123ca7bea34a934c40820aeaf9eb132c2000";
    private const string EmptyVault2674 = "00000a00000000000000000000000000";

    [Fact]
    public void Refusal2674_IsRetailsFrame()
    {
        Assert.Equal(Refused2674, Convert.ToHexString(GuildStorageOpenHandler.BuildOutOfRangeRefusal2674()).ToLowerInvariant());
        Assert.Equal(GuildStorageOpenHandler.ResultOutOfRange,
            BinaryPrimitives.ReadUInt16LittleEndian(Convert.FromHexString(Refused2674)));
    }

    [Fact]
    public void EmptyVault2674_IsRetailsFrame()
    {
        
        Assert.Equal(EmptyVault2674, Convert.ToHexString(GuildPackets.BuildGuildStorageList(new Guild())).ToLowerInvariant());
    }

    [Theory]
    [InlineData(Refused1489, 0x500000ECu, false)]
    [InlineData(Answered1489, 0x00202C13u, true)]
    public void Request1489_CarriesTheNpcHandleAfterTheGuid(string hex, uint handle, bool npcTagged)
    {
        var body = Convert.FromHexString(hex);
        Assert.Equal(GuildStorageOpenHandler.RequestLength, body.Length);
        var read = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(GuildStorageOpenHandler.NpcHandleOffset));
        Assert.Equal(handle, read);
        
        Assert.Equal(npcTagged, (read & 0xFF) == NpcStateManager.NpcActorTag);
    }
}

/// <summary>
/// Which sessions a vault change reaches: the mover, every other guild member with the vault window open (answered 1489, no 1490 yet), and no one else. The vault window registry is process-wide, so each test uses its own session ids.
/// </summary>
public class GuildVaultWindowsTests
{
    private static Guild GuildOf(params uint[] charIds)
    {
        var g = new Guild();
        foreach (var c in charIds) g.Members.Add(new GuildMember { CharId = c });
        return g;
    }

    private static Func<uint, PlayerState?> Lookup(Dictionary<uint, PlayerState> bySession)
        => sid => bySession.GetValueOrDefault(sid);

    private static PlayerState Player(uint sessionId, uint charId) => new() { SessionId = sessionId, CharacterId = charId };

    [Fact]
    public void OtherMembersWithTheWindowOpen_AreTheViewers()
    {
        
        
        const uint withdrawer = 0xA1000101, watcher = 0xA1000102, closed = 0xA1000103, neverOpened = 0xA1000104, outsider = 0xA1000105;
        var players = new Dictionary<uint, PlayerState>
        {
            [withdrawer] = Player(withdrawer, 91001), [watcher] = Player(watcher, 91002), [closed] = Player(closed, 91003),
            [neverOpened] = Player(neverOpened, 91004), [outsider] = Player(outsider, 91999),
        };
        var lookup = Lookup(players);
        var guild = GuildOf(91001, 91002, 91003, 91004);

        GuildVaultWindows.Open(withdrawer, 91001, lookup);
        GuildVaultWindows.Open(watcher, 91002, lookup);
        GuildVaultWindows.Open(closed, 91003, lookup);
        GuildVaultWindows.Close(closed);                     
        GuildVaultWindows.Open(outsider, 91999, lookup);     

        Assert.Equal(new[] { watcher }, GuildVaultWindows.OtherViewers(guild, withdrawer, lookup));
        
        Assert.Equal(new[] { withdrawer }, GuildVaultWindows.OtherViewers(guild, watcher, lookup));

        foreach (var sid in new[] { withdrawer, watcher, outsider }) GuildVaultWindows.Close(sid);
        Assert.Empty(GuildVaultWindows.OtherViewers(guild, withdrawer, lookup));
    }

    [Fact]
    public void EndedSession_IsDropped()
    {
        const uint mover = 0xA1000201, gone = 0xA1000202;
        var players = new Dictionary<uint, PlayerState> { [mover] = Player(mover, 92001), [gone] = Player(gone, 92002) };
        var lookup = Lookup(players);
        var guild = GuildOf(92001, 92002);
        GuildVaultWindows.Open(gone, 92002, lookup);
        Assert.Equal(new[] { gone }, GuildVaultWindows.OtherViewers(guild, mover, lookup));

        players.Remove(gone);   
        Assert.Empty(GuildVaultWindows.OtherViewers(guild, mover, lookup));
        
        
        players[gone] = Player(gone, 92002);
        Assert.Empty(GuildVaultWindows.OtherViewers(guild, mover, lookup));
    }

    [Fact]
    public void SessionHoldingAnotherCharacter_IsNotAViewer()
    {
        const uint mover = 0xA1000301, reused = 0xA1000302;
        var players = new Dictionary<uint, PlayerState> { [mover] = Player(mover, 93001), [reused] = Player(reused, 93002) };
        var lookup = Lookup(players);
        var guild = GuildOf(93001, 93002, 93003);
        GuildVaultWindows.Open(reused, 93002, lookup);

        players[reused] = Player(reused, 93003);   
        Assert.Empty(GuildVaultWindows.OtherViewers(guild, mover, lookup));
        players[reused] = Player(reused, 93002);   
        Assert.Empty(GuildVaultWindows.OtherViewers(guild, mover, lookup));
    }

    [Fact]
    public void MemberWhoLeftTheGuild_IsNotAViewer()
    {
        const uint mover = 0xA1000401, leaver = 0xA1000402;
        var players = new Dictionary<uint, PlayerState> { [mover] = Player(mover, 94001), [leaver] = Player(leaver, 94002) };
        var lookup = Lookup(players);
        var guild = GuildOf(94001, 94002);
        GuildVaultWindows.Open(leaver, 94002, lookup);
        Assert.Equal(new[] { leaver }, GuildVaultWindows.OtherViewers(guild, mover, lookup));

        guild.Members.RemoveAll(m => m.CharId == 94002);
        Assert.Empty(GuildVaultWindows.OtherViewers(guild, mover, lookup));
        GuildVaultWindows.Close(leaver);
    }
}

/// <summary>1327 ReqRepairItem: the repair price formula and the 2336 refusal.</summary>
public class RepairPricingTests
{
    [Fact]
    public void Constants_AreTheDoublesInRdata()
    {
        
        Assert.Equal(0x3F1A36E2E0000000L, BitConverter.DoubleToInt64Bits(RepairPricing.PerPointRate));
        Assert.Equal(0x3FD1EB8520000000L, BitConverter.DoubleToInt64Bits(RepairPricing.GradeFactor(4)));
        Assert.Equal(0x3FCB22D0E0000000L, BitConverter.DoubleToInt64Bits(RepairPricing.GradeFactor(3)));
        Assert.Equal(0x3FBAE147A0000000L, BitConverter.DoubleToInt64Bits(RepairPricing.GradeFactor(2)));
        foreach (byte g in new byte[] { 0, 1, 5 })
            Assert.Equal(0x3FB99999A0000000L, BitConverter.DoubleToInt64Bits(RepairPricing.GradeFactor(g)));
    }

 /// <summary>
 /// Repairs whose debit the price formula reproduces exactly, at the 5 % guild discount. Wallet and durability inputs come from the preceding packets of the session.
 /// </summary>
    public static TheoryData<string, uint[], int[], long, long> RetailRepairs => new()
    {
        
        { "c9f4656e-00c91607 01:15:31.432", new uint[] { 16702290 }, new[] { 6970 }, 4290717966L, 4290677667L },
        
        { "c9f4656e-00195af7 07:14:05.362",
            new uint[] { 16702315, 16702311, 16702312, 16701772, 16702314, 16702297 },
            new[] { 9536, 9536, 9536, 9536, 9536, 9391 }, 5486999379L, 5486979349L },
        
        { "c9f4656e-000577d7 18:24:10.814",
            new uint[] { 16701811, 16702360, 16702361, 16702362, 16702363, 16702304, 16702305, 16702321, 16702358, 16702357, 16702357, 16702359 },
            new[] { 8651, 8651, 8651, 8651, 8651, 8495, 8495, 9050, 9050, 9050, 9050, 9050 }, 2787330494L, 2787251538L },
    };

    [Theory]
    [MemberData(nameof(RetailRepairs))]
    public async Task RetailDebit_IsTheClientFormulaAtFivePercent(string frame, uint[] templates, int[] durability, long walletBefore, long walletAfter)
    {
        var items = await UnitA10Data.ItemsAsync(templates);
        long total = 0;
        for (int i = 0; i < templates.Length; i++)
            total += RepairPricing.ItemCost(items[templates[i]], durability[i]);
        Assert.Equal((frame, walletBefore - walletAfter), (frame, RepairPricing.ApplyDiscount(total, 5)));
    }

    [Fact]
    public async Task ItemCost_SingleItem_MatchesTheClientTerm()
    {
        
        var items = await UnitA10Data.ItemsAsync(16702290u);
        Assert.Equal(42419L, RepairPricing.ItemCost(items[16702290], 6970));
        Assert.Equal(40299L, RepairPricing.ApplyDiscount(42419, 5));
        Assert.Equal(42419L, RepairPricing.ApplyDiscount(42419, 0));
        Assert.Equal(0L, RepairPricing.ItemCost(items[16702290], 10000));   
        Assert.Equal(0L, RepairPricing.ItemCost(items[16702290], -1));      
    }

    [Fact]
    public async Task ItemCost_IsAtLeastOne()
    {
        
        var items = await UnitA10Data.ItemsAsync(11100001u);
        var sword = items[11100001];
        Assert.Equal(1L, RepairPricing.ItemCost(sword, (int)sword.Durability - 1));
    }

    [Fact]
    public async Task Quivers_AreNeverPriced()
    {
        
        
        var quivers = await UnitA10Data.ItemsAsync(r =>
            r.GetProperty("Equip_Type").GetInt32() == RepairPricing.UnrepairedEquipType && r.GetProperty("Durability").GetInt64() > 0);
        Assert.Equal(145, quivers.Count);
        foreach (var q in quivers.Values)
            Assert.Equal(0L, RepairPricing.ItemCost(q, (int)q.Durability / 2));
    }

    [Fact]
    public async Task DiscountPercent_ReadsGuildSkillCategoryFive()
    {
        await using var fs = File.OpenRead(UnitA10Data.DataFile("Guild_Skill_Level.json"));
        var rows = (await JsonSerializer.DeserializeAsync<List<GuildSkillLevel>>(fs))!;
        var table = GuildSkillLearnPackets.Index(rows);
        Assert.Equal(0L, RepairPricing.DiscountPercent(Array.Empty<uint>(), table));
        Assert.Equal(1L, RepairPricing.DiscountPercent(new uint[] { 120501 }, table));
        Assert.Equal(4L, RepairPricing.DiscountPercent(new uint[] { 120501, 120504 }, table));
        Assert.Equal(5L, RepairPricing.DiscountPercent(new uint[] { 120505 }, table));
        
        var storage = rows.First(r => r.Skill_Category_1 == GuildFundsInHandPackets.SkillCategoryStorageLimit
                                      || r.Skill_Category_2 == GuildFundsInHandPackets.SkillCategoryStorageLimit);
        Assert.Equal(0L, RepairPricing.DiscountPercent(new[] { storage.Guild_Skill_ID }, table));
    }

    [Theory]
    
    [InlineData(RepairPricing.ResultNoItem, "d5000000624e")]
    
    [InlineData(RepairPricing.ResultNotNpc, "d5000000614e")]
    public void Refusal2336_MatchesRetailsFirst64Bytes(ushort code, string retailPrefix)
    {
        var body = RepairPricing.BuildRepairRefusal2336(code);
        Assert.Equal(217, body.Length);   
        
        var retail64 = retailPrefix + new string('0', 2 * (64 - retailPrefix.Length / 2));
        Assert.Equal(retail64, Convert.ToHexString(body, 0, 64).ToLowerInvariant());
        Assert.All(body.AsSpan(64).ToArray(), b => Assert.Equal(0, b));
    }
}

/// <summary>1535 ReqItemTransform: the room check performed instead of consuming the source into a full bag.</summary>
public class ItemTransformRoomTests
{
 /// <summary>An unanswered transform pair from the reference data: source 70000882 (count 6) to target 34000453, an ItemTransform row.</summary>
    private const uint Source = 70000882;
    private const uint Target = 34000453;

    private static List<InventoryItem> FullMainBag(int sourceCount, out InventoryItem source)
    {
        var bag = new List<InventoryItem>();
        for (short s = 0; s < BagCapacity.MainBagCells; s++)
            bag.Add(new InventoryItem { Id = 1000u + (ulong)s, ItemId = 30000001, Count = 1, Slot = s, StorageType = 0 });   
        source = bag[5];
        source.ItemId = Source;
        source.Count = sourceCount;
        return bag;
    }

    [Fact]
    public async Task FullBag_NewStack_HasNoRoom()
    {
        var items = await UnitA10Data.ItemsAsync(Target);
        var bag = FullMainBag(6, out var source);
        Assert.Null(ItemTransformHandler.TargetCells(bag, items, source, Target, 1, items[Target].Stack_Max));
    }

    [Fact]
    public async Task FullBag_LastSourceUnit_FreesItsCell()
    {
        var items = await UnitA10Data.ItemsAsync(Target);
        var bag = FullMainBag(1, out var source);
        var cells = ItemTransformHandler.TargetCells(bag, items, source, Target, 1, items[Target].Stack_Max);
        Assert.NotNull(cells);
        Assert.Equal(new short[] { source.Slot }, cells);
    }

    [Fact]
    public async Task FullBag_TopUp_NeedsNoCell()
    {
        var items = await UnitA10Data.ItemsAsync(Target);
        var bag = FullMainBag(6, out var source);
        bag[7].ItemId = Target;
        bag[7].Count = items[Target].Stack_Max - 1;   
        Assert.NotNull(ItemTransformHandler.TargetCells(bag, items, source, Target, 1, items[Target].Stack_Max));

        bag[7].Count = items[Target].Stack_Max;       
        Assert.Null(ItemTransformHandler.TargetCells(bag, items, source, Target, 1, items[Target].Stack_Max));
    }

    [Fact]
    public async Task FreeCells_AreLowestFirst()
    {
        var items = await UnitA10Data.ItemsAsync(Target);
        var bag = FullMainBag(6, out var source);
        bag.RemoveAll(i => i.Slot is 3 or 12);
        var cells = ItemTransformHandler.TargetCells(bag, items, source, Target, 1, items[Target].Stack_Max);
        Assert.Equal(new short[] { 3, 12 }, cells);
    }
}
