using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using NameFilterRow = RO2.Domain.GameData.Tables.NameFilter;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Rename availability checks for 1564 ReqCanRenameChaName and 1563 ReqRenameChaName (<see cref="CharacterRename"/>).
/// The expected 4-byte result is the 2814 AnsCanRenameChaName code returned for each request body.
/// </summary>
public class CharacterRenameCodesTests
{
    
    private static readonly IReadOnlyList<NameFilterRow> Filter = [new() { name = "GM" }, new() { name = "RO2" }];

    private const uint Me = 7;
    private const uint RenameScroll = 70000006;   
    private const uint NotAScroll = 30000272;     

    [Theory]
    
    [InlineData("34ebace2a73d040e0201076c004f004d004500470041006c000201042d0061002d006100", "lOMEGAl", "2b3c0000")]
    
    [InlineData("34ebace2a73d040e0201076c004f004d004500470041006c0002010f610073006400610073006400610073006400610073006400730066006400", "lOMEGAl", "2b3c0000")]
    
    [InlineData("05d1c6fca63d040e02010c690052004f00430072006500630065006e0074006900610002010261006100", "iROCrecentia", "2a3c0000")]
    
    
    [InlineData("05d1c6fca63d040e02010c690052004f00430072006500630065006e0074006900610002010b540065006d0070006500730074005500730065007200", "TempestUser", "2a3c0000")]
    
    [InlineData("34ebace2a73d040e0201076c004f004d004500470041006c0002010d4200720061006e006400690073006800530074006f0072006d00", "lOMEGAl", "00000000")]
    
    [InlineData("05d1c6fca63d040e02010c690052004f00430072006500630065006e0074006900610002010d54004b0054004b004b0045004b004c00500053004c0050004500", "iROCrecentia", "00000000")]
    
    [InlineData("34ebace2a73d040e0201076c004f004d004500470041006c0002010b690052004f003200570061007200720069006f007200", "lOMEGAl", "00000000")]
    
    [InlineData("34ebace2a73d040e0201076c004f004d004500470041006c00020106690052004f00320047004d00", "lOMEGAl", "2b3c0000")]
    
    [InlineData("34ebace2a73d040e0201076c004f004d004500470041006c00020107690052004f00320047004d003100", "lOMEGAl", "2b3c0000")]
    
    [InlineData("34ebace2a73d040e0201076c004f004d004500470041006c00020105690052004f0032004100", "lOMEGAl", "00000000")]
    public void NameRules_GiveRetailsAnswer(string requestHex, string playerName, string retail2814Hex)
    {
        Assert.True(CharacterRename.TryParse(Convert.FromHexString(requestHex), out var request));
        var (result, _) = CharacterRename.CheckName(request.NewName, playerName, Filter);
        Assert.Equal(retail2814Hex, Hex(result));
    }

    [Fact]
    public async Task ScrollUidZero_IsRefusedWith15402()
    {
        
        
        var body = Convert.FromHexString("0000000000000000020100020108610073007100310032007a007a006a00");
        Assert.True(CharacterRename.TryParse(body, out var request));
        var (result, scroll, _) = await CharacterRename.CheckAsync(Player("lOMEGAl"), request, await ServicesAsync());
        Assert.Equal("2a3c0000", Hex(result));
        Assert.Null(scroll);
    }

    [Fact]
    public async Task TakenName_IsRefusedWith15402()
    {
        
        
        var body = Convert.FromHexString(
            "5c8dd18ba73d040e02010a4b00610079006100740065004e006f007600610002010e4d006f006f006e006c006900670068007400440061006e0063006500");
        Assert.True(CharacterRename.TryParse(body, out var request));
        Assert.Equal(0u, CharacterRename.CheckName(request.NewName, "KayateNova", Filter).result);
        var services = await ServicesAsync(taken: "MoonlightDance", Scroll(request.ScrollUid, RenameScroll));
        var (result, scroll, _) = await CharacterRename.CheckAsync(Player("KayateNova"), request, services);
        Assert.Equal("2a3c0000", Hex(result));
        Assert.Null(scroll);
    }

    [Fact]
    public async Task FreeNameWithScroll_IsAccepted_AndReturnsTheScroll()
    {
        
        var body = Convert.FromHexString(
            "34ebace2a73d040e0201076c004f004d004500470041006c0002010d4200720061006e006400690073006800530074006f0072006d00");
        Assert.True(CharacterRename.TryParse(body, out var request));
        var services = await ServicesAsync(taken: null, Scroll(request.ScrollUid, RenameScroll));
        var (result, scroll, _) = await CharacterRename.CheckAsync(Player("lOMEGAl"), request, services);
        Assert.Equal("00000000", Hex(result));
        Assert.Equal(request.ScrollUid, scroll?.Id);
    }

    [Fact]
    public async Task ItemThatIsNotANameChangeScroll_IsRefusedWith15402()
    {
        var body = Convert.FromHexString(
            "34ebace2a73d040e0201076c004f004d004500470041006c0002010d4200720061006e006400690073006800530074006f0072006d00");
        Assert.True(CharacterRename.TryParse(body, out var request));
        var services = await ServicesAsync(taken: null, Scroll(request.ScrollUid, NotAScroll));
        var (result, scroll, _) = await CharacterRename.CheckAsync(Player("lOMEGAl"), request, services);
        Assert.Equal(CharacterRename.ResultRefused, result);
        Assert.Null(scroll);
    }

    [Fact]
    public void Codes_AreTheClientSwitchCases()
    {
        
        Assert.Equal(15401u, CharacterRename.ResultFailed);
        Assert.Equal(15402u, CharacterRename.ResultRefused);
        Assert.Equal(15403u, CharacterRename.ResultNotAllowed);
    }

    private static string Hex(uint result)
    {
        var wire = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(wire, result);
        return Convert.ToHexString(wire).ToLowerInvariant();
    }

    private static PlayerState Player(string name) => new() { CharacterId = Me, Name = name };

    private static InventoryItem Scroll(ulong uid, uint itemId) =>
        new() { Id = uid, CharacterId = Me, ItemId = itemId, Count = 1, StorageType = 0, Slot = 22 };

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

    private static async Task<GameDataStore> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":70000006,"Item_Type":108,"Stack_Max":1},
                 {"ID":30000272,"Stack_Max":99}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "name_filter.json"), """
                [{"name":"GM"},{"name":"RO2"}]
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

    private static async Task<IServiceProvider> ServicesAsync(string? taken = null, params InventoryItem[] bag)
    {
        var characters = DispatchProxy.Create<ICharacterRepository, NameLookup>();
        ((NameLookup)(object)characters).Taken = taken;
        var collection = new ServiceCollection();
        collection.AddSingleton(await Store.Value);
        collection.AddSingleton<IInventoryRepository>(new Bag(bag));
        collection.AddSingleton(characters);
        return collection.BuildServiceProvider();
    }

 /// <summary>A character repository that only answers the name lookup the check makes.</summary>
    public class NameLookup : DispatchProxy
    {
        public string? Taken { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(ICharacterRepository.CharacterNameExistsAsync)
                ? Task.FromResult(args is [string name] && string.Equals(name, Taken, StringComparison.Ordinal))
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class Bag(InventoryItem[] items) : IInventoryRepository
    {
        private readonly List<InventoryItem> _items = items.ToList();
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(_items.Where(i => i.CharacterId == characterId).ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(_items.FirstOrDefault(i => i.Id == id));
        public Task AddItemAsync(InventoryItem item) { _items.Add(item); return Task.CompletedTask; }
        public Task RemoveItemAsync(ulong itemId) { _items.RemoveAll(i => i.Id == itemId); return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(_items.Count == 0 ? 1UL : _items.Max(i => i.Id) + 1);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }
}
