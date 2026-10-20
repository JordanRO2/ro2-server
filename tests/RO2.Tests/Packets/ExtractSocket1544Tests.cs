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
/// 1544 ReqExtractSocket refusals and rune placement (<see cref="ExtractSocketHandler.Check"/>,
/// <see cref="ExtractSocketHandler.PlaceExtracted"/>, <see cref="ExtractSocketHandler.BuildRefusal2787"/>).
/// </summary>
/// <remarks>
/// Successful extractions answer 2787 with 254-byte bodies. Refusal codes map to: 1 rune puncher not found,
/// 2 target item not found, 4 wrong socket slot index, 7 item storage not retrieved. Items come from the
/// shipped data: Rune Pincers (60000203), the rune 22264024 and the sword 11100001.
/// </remarks>
public class ExtractSocket1544Tests
{
    private const uint Owner = 7;
    private const uint Pincers = 60000203;
    private const uint Rune = 22264024;

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

    private static async Task<GameDataStore> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-extractsocket-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":60000203,"Item_Type":50,"Stack_Max":20},{"ID":22264024,"Item_Type":29,"Stack_Max":255},
                 {"ID":11100001,"Item_Type":1,"Stack_Max":1,"Socket_GroupID":1}]
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

    private static InventoryItem Item(ulong id, uint itemId, short slot, int count = 1, byte storage = 0, string? holes = null) =>
        new() { Id = id, CharacterId = Owner, ItemId = itemId, Count = count, StorageType = storage, Slot = slot, CardsJson = holes };

 /// <summary>A 1544 body: [u64 tool][u64 target][u8 hole].</summary>
    private static (ulong Tool, ulong Target, int Hole) Request(string hex)
    {
        var b = Convert.FromHexString(hex);
        Assert.Equal(17, b.Length);
        return (BinaryPrimitives.ReadUInt64LittleEndian(b), BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8)), b[16]);
    }

    private static string Refusal(string codeHex) => codeHex + new string('0', 2 * 252);

    [Fact]
    public async Task PincersOnAnOpenedEmptyHole_Code4_IsRetailsFrame()
    {
        
        
        var store = await Store.Value;
        var (toolUid, targetUid, hole) = Request("b76d860ea83d040ef6ea0aaaa73d040e00");
        var tool = Item(toolUid, Pincers, 52, count: 5);
        var target = Item(targetUid, 11100001, 51, holes: "[0]");

        var code = ExtractSocketHandler.Check(store, Owner, tool, target, hole, out _);

        Assert.Equal(ExtractSocketHandler.WrongSocketIndex, code);
        Assert.Equal(Convert.FromHexString(Refusal("0400")), ExtractSocketHandler.BuildRefusal2787(code));
    }

    [Fact]
    public async Task ARuneSentAsTheTool_Code1_IsRetailsFrame()
    {
        
        
        var store = await Store.Value;
        var (toolUid, targetUid, hole) = Request("4e5fa609a83d040eace3e7faa73d040e02");
        var tool = Item(toolUid, Rune, 53, count: 3);
        var target = Item(targetUid, 11100001, 51, holes: "[22264024,22264024,22264024]");

        var code = ExtractSocketHandler.Check(store, Owner, tool, target, hole, out _);

        Assert.Equal(ExtractSocketHandler.PincersNotFound, code);
        Assert.Equal(Convert.FromHexString(Refusal("0100")), ExtractSocketHandler.BuildRefusal2787(code));
    }

    [Fact]
    public async Task APincerStackAlreadyGone_Code1()
    {
        
        
        var store = await Store.Value;
        var (_, targetUid, hole) = Request("83588d0ea83d040e7d7d9dc4a73d040e01");
        var target = Item(targetUid, 11100001, 51, holes: "[22264024,22264024]");

        Assert.Equal(ExtractSocketHandler.PincersNotFound, ExtractSocketHandler.Check(store, Owner, null, target, hole, out _));
        Assert.Equal(ExtractSocketHandler.PincersNotFound,
            ExtractSocketHandler.Check(store, Owner, Item(1, Pincers, 52, count: 0), target, hole, out _));
        Assert.Equal(ExtractSocketHandler.PincersNotFound,
            ExtractSocketHandler.Check(store, Owner + 1, Item(1, Pincers, 52), target, hole, out _));
        Assert.Equal(ExtractSocketHandler.PincersNotFound,
            ExtractSocketHandler.Check(store, Owner, Item(1, Pincers, 52, storage: 2), target, hole, out _));
    }

    [Fact]
    public async Task AMissingTarget_Code2_AClosedHole_Code4_AFilledHole_Extracts()
    {
        var store = await Store.Value;
        var tool = Item(1, Pincers, 52, count: 5);

        Assert.Equal(ExtractSocketHandler.TargetNotFound, ExtractSocketHandler.Check(store, Owner, tool, null, 0, out _));
        Assert.Equal(ExtractSocketHandler.TargetNotFound,
            ExtractSocketHandler.Check(store, Owner, tool, Item(2, 11100001, 3, storage: 2, holes: "[22264024]"), 0, out _));
        Assert.Equal(ExtractSocketHandler.WrongSocketIndex,
            ExtractSocketHandler.Check(store, Owner, tool, Item(2, 11100001, 51, holes: null), 0, out _));
        Assert.Equal(ExtractSocketHandler.WrongSocketIndex,
            ExtractSocketHandler.Check(store, Owner, tool, Item(2, 11100001, 51, holes: "[22264024]"), 1, out _));
        Assert.Equal(ExtractSocketHandler.Extracted,
            ExtractSocketHandler.Check(store, Owner, tool, Item(2, 11100001, 51, holes: "[22264024]"), 0, out _));
        Assert.Equal(ExtractSocketHandler.Extracted,
            ExtractSocketHandler.Check(store, Owner, tool, Item(2, 11100001, 5, storage: 1, holes: "[0,22264024]"), 1, out _));
    }

    [Fact]
    public async Task TheRuneJoinsAStackThatStillFits()
    {
        
        var store = await Store.Value;
        var tool = Item(1, Pincers, 0, count: 5);
        var stack = Item(3, Rune, 4, count: 17);
        var owned = new List<InventoryItem> { tool, Item(2, 11100001, 1), stack };

        var (join, cell) = ExtractSocketHandler.PlaceExtracted(owned, tool, Rune, store);

        Assert.Same(stack, join);
        Assert.Equal(-1, cell);
    }

    [Fact]
    public async Task AFullStack_OpensANewStackInTheFirstFreeCell()
    {
        var store = await Store.Value;
        var tool = Item(1, Pincers, 0, count: 5);
        var owned = new List<InventoryItem> { tool, Item(2, 11100001, 1), Item(3, Rune, 2, count: 255) };

        var (join, cell) = ExtractSocketHandler.PlaceExtracted(owned, tool, Rune, store);

        Assert.Null(join);
        Assert.Equal((short)3, cell);
    }

    [Fact]
    public async Task AFullBag_HasNoCell_UnlessTheLastPincersFreeOne()
    {
        var store = await Store.Value;
        
        var owned = Enumerable.Range(0, BagCapacity.MainBagCells)
            .Select(s => Item((ulong)(100 + s), 11100001, (short)s)).ToList();
        var tool = Item(200, Pincers, 7, count: 5);
        owned[7] = tool;

        var (join, cell) = ExtractSocketHandler.PlaceExtracted(owned, tool, Rune, store);
        Assert.Null(join);
        Assert.Equal(-1, cell);
        Assert.Equal(7, ExtractSocketHandler.NoBagCell);

        
        
        tool.Count = 1;
        (join, cell) = ExtractSocketHandler.PlaceExtracted(owned, tool, Rune, store);
        Assert.Null(join);
        Assert.Equal(7, cell);
    }
}

/// <summary>
/// Tests 1428 ReqGuildNoticeUpdate: the length refusal and the 2563 and 2564 bodies.
/// </summary>
public class GuildNotice1428Tests
{
    private static readonly byte[] Guid2dfbcba0 = Convert.FromHexString("2dfbcba076b8434a815d7f4cf2b6c68a");

 /// <summary>A 1428 notice of 210 bytes (93 characters plus U+F801). The answer is 2563 0000 with the
 /// guild id, and a 2564 identical to the request is sent to both members.</summary>
    private const string Accepted1428 =
        "ce0000002dfbcba076b8434a815d7f4cf2b6c68a5e0057006f0045002000740069006d0065003a0020005700650064002000300037002e003000300020005300540020004900" +
        "490020005300610074002f00530075006e002000310037002e003000300020005300540020004900490020004a006f0069006e0020006f007500720020004400690073006300" +
        "6f00720064003a002000680074007400700073003a002f002f0064006900730063006f00720064002e00670067002f0051007a007300670037004500320042005700320001f8";

 /// <summary>A 1428 notice of 224 bytes (100 characters plus U+F801) is answered with 2563 0f00 and no
 /// 2564; the previous notice remains.</summary>
    private const string Refused1428 =
        "dc0000002dfbcba076b8434a815d7f4cf2b6c68a6500570065006c0063006f006d0065002100200049004900200057006f0045003a0020005700650064002000300037002e00" +
        "3000300020004900490020005300610074002f00530075006e002000310037002e003000300020002000200020002000200020004900490020004a006f0069006e0020006f00" +
        "75007200200044006900730063006f00720064003a002000680074007400700073003a002f002f0064006900730063006f00720064002e00670067002f0051007a0073006700" +
        "37004500320042005700320001f8";

    [Fact]
    public void ANoticeOf101Chars_IsRefused15_WithTheGuildGuid()
    {
        var body = Convert.FromHexString(Refused1428);
        Assert.Equal(224, body.Length);
        var (count, text) = GuildNoticeUpdateHandler.ReadNotice(body);

        Assert.Equal(101, count);
        Assert.Equal(100, text.Length);   
        Assert.Equal(GuildNoticeUpdateHandler.NoticeTooLong, GuildNoticeUpdateHandler.Validate(inGuild: true, count));
        Assert.Equal(Convert.FromHexString("0f00" + "2dfbcba076b8434a815d7f4cf2b6c68a"),
            GuildNoticeUpdateHandler.BuildAnsGuildUpdateNotice(GuildNoticeUpdateHandler.NoticeTooLong, Guid2dfbcba0));
    }

    [Fact]
    public void AnAcceptedNotice_Answers0000_AndBroadcastsRetails2564()
    {
        var request = Convert.FromHexString(Accepted1428);
        var (count, text) = GuildNoticeUpdateHandler.ReadNotice(request);

        Assert.Equal(94, count);
        Assert.Equal(GuildNoticeUpdateHandler.NoticeSet, GuildNoticeUpdateHandler.Validate(inGuild: true, count));
        Assert.Equal(Convert.FromHexString("0000" + "2dfbcba076b8434a815d7f4cf2b6c68a"),
            GuildNoticeUpdateHandler.BuildAnsGuildUpdateNotice(GuildNoticeUpdateHandler.NoticeSet, Guid2dfbcba0));

        
        var guild = new Guild { Guid16 = (byte[])Guid2dfbcba0.Clone(), Notice = text };
        Assert.Equal(request, GuildPackets.BuildNfyGuildNoticeUpdated(guild));
    }

    [Fact]
    public void TheCeilingSitsBetweenRetailsLargestAcceptedAndItsRefusal()
    {
        
        Assert.Equal(GuildNoticeUpdateHandler.NoticeSet, GuildNoticeUpdateHandler.Validate(true, 99));
        Assert.Equal(GuildNoticeUpdateHandler.NoticeSet, GuildNoticeUpdateHandler.Validate(true, GuildNoticeUpdateHandler.MaxNoticeCount));
        Assert.Equal(GuildNoticeUpdateHandler.NoticeTooLong, GuildNoticeUpdateHandler.Validate(true, 101));
        
        Assert.Equal(GuildNoticeUpdateHandler.NoGuild, GuildNoticeUpdateHandler.Validate(false, 20));
        Assert.Equal(Convert.FromHexString("0700" + new string('0', 32)),
            GuildNoticeUpdateHandler.BuildAnsGuildUpdateNotice(GuildNoticeUpdateHandler.NoGuild, null));
    }
}

/// <summary>1430 AnsGuildInviteYou: an answer to an invite past its lifetime is refused with code 12.</summary>
public class GuildInviteAnswer1430Tests
{
 /// <summary>The repository's data folder, found by walking up from the test binaries.</summary>
    private static string DataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Config_Def.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Config_Def.json");
    }

    [Fact]
    public async Task TheLifetimeIsConfigDef5_300Seconds()
    {
        await using var fs = File.OpenRead(Path.Combine(DataDir(), "Config_Def.json"));
        var config = (await JsonSerializer.DeserializeAsync<List<ConfigDef>>(fs))!;

        Assert.Equal(TimeSpan.FromSeconds(300), GuildInviteYouAnsHandler.AnswerLifetime(config));
        Assert.Null(GuildInviteYouAnsHandler.AnswerLifetime(Array.Empty<ConfigDef>()));
    }

    [Theory]
    
    
    [InlineData("d003e843ff6eee4d93467b13c0bd4fdd8d684bb8f08fd14da2c744fae9c1e07c01",
                "0c00d003e843ff6eee4d93467b13c0bd4fdd8d684bb8f08fd14da2c744fae9c1e07c01", 978.690)]
    
    [InlineData("227fe505d322514ab34251789c7f171be34c69a55285744c9b18d57ba30d6a0501",
                "0c00227fe505d322514ab34251789c7f171be34c69a55285744c9b18d57ba30d6a0501", 4314.748)]
    public void AnAnswerPastTheLifetime_IsRetailsCode12Frame(string request1430, string retail2568, double ageSeconds)
    {
        var created = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        var invite = new GuildPendingInvite { GuildId = 1, InviterCharId = 2, CreatedUtc = created };
        var lifetime = TimeSpan.FromSeconds(300);

        Assert.True(GuildInviteYouAnsHandler.IsExpired(invite, lifetime, created.AddSeconds(ageSeconds)));
        var code = GuildInviteYouAnsHandler.RefusalCode(accepted: true, validInvite: false);
        Assert.Equal(GuildInviteYouAnsHandler.NoInvite, code);
        Assert.Equal(Convert.FromHexString(retail2568),
            GuildInviteYouAnsHandler.BuildAnsGuildInviteYouAns(code, Convert.FromHexString(request1430)));
    }

    [Fact]
    public void RetailsSlowestAcceptedAnswer_IsStillValid()
    {
        
        var created = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        var invite = new GuildPendingInvite { CreatedUtc = created };
        var lifetime = TimeSpan.FromSeconds(300);

        Assert.False(GuildInviteYouAnsHandler.IsExpired(invite, lifetime, created.AddSeconds(274.453)));
        Assert.True(GuildInviteYouAnsHandler.IsExpired(invite, lifetime, created.AddSeconds(300)));
        Assert.False(GuildInviteYouAnsHandler.IsExpired(invite, null, created.AddDays(1)));
        Assert.Equal(GuildInviteYouAnsHandler.AnswerTaken, GuildInviteYouAnsHandler.RefusalCode(accepted: false, validInvite: true));
        Assert.Equal(GuildInviteYouAnsHandler.AlreadyInAGuild, GuildInviteYouAnsHandler.RefusalCode(accepted: true, validInvite: true));
        Assert.Equal(GuildInviteYouAnsHandler.NoInvite, GuildInviteYouAnsHandler.RefusalCode(accepted: false, validInvite: false));
    }
}

/// <summary>
/// Tests the 1587 and 1489 vault lists (<see cref="GuildPackets.BuildGuildStorageList"/>): every stored record
/// is included, with no 81-record ceiling.
/// </summary>
public class GuildStorageList1587Tests
{
    private static Guild VaultOf(int items)
    {
        var g = new Guild { Gold = 200_000_000 };
        for (int i = 0; i < items; i++)
            g.Storage.Add(new GuildStorageItem { Uid = (ulong)(1000 + i), ItemId = 22264024, Count = 1, StorageSlot = (byte)i });
        return g;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(81)]
    [InlineData(82)]
    [InlineData(100)]
    public void TheList_CarriesEveryStoredRecord(int items)
    {
        
        var body = GuildPackets.BuildGuildStorageList(VaultOf(items));
        Assert.Equal(16 + items * 126, body.Length);
        Assert.Equal((uint)(body.Length - 6), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2)));
        Assert.Equal(200_000_000UL, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(6)));
        Assert.Equal(items, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(14)));
    }
}

/// <summary>1688 ReqGuildHouseResult: the empty answer, since no guild-house draw system exists.</summary>
public class GuildHouseResult1688Tests
{
    [Fact]
    public void TheEmptyAnswer_IsRetails4Bytes()
    {
        
        Assert.Equal(Convert.FromHexString("00000100"), GuildHouseResultHandler.BuildEmptyResult());
    }
}
