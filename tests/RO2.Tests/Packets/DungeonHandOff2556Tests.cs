using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the 1531 ReqPartyDungeonTeleport answer: 2938, a self 2505, and the 81-byte 2556 AnsChannelMove.
/// The destination block (map, position, direction) is the DungeonInfo row of the offered level at its
/// Position_Info point (<see cref="DungeonChannelHandOff"/>, <see cref="PartyDungeonTeleportHandler.ResolveArrival"/>).
/// </summary>
public class DungeonHandOff2556Tests
{
 /// <summary>The 2556 answering the 1531 of group 42, which offered level 3.</summary>
    private const string Retail2556 =
        "1ff04c000000"                                                          
        + "09c2ef3d3bd2da4e81ab1ceff12ee7b8"                                    
        + "3100320038002e003200340031002e00390033002e0032003100310000000000"    
        + "321c"                                                                
        + "77800600" + "5cafb143d7a30e4300005242" + "5a"                        
        + "1c42e8a7b3b6e00d";                                                   

    [Fact]
    public async Task Builder_ReproducesTheRetailHandOff()
    {
        var data = await DungeonDataAsync();
        var (row, arrival) = PartyDungeonTeleportHandler.ResolveArrival(data, 42, 3)!.Value;
        Assert.Equal(426103u, arrival.Area_ID);

        var body = DungeonChannelHandOff.BuildAnsChannelMove2556(0x004CF01Fu,
            Convert.FromHexString("09c2ef3d3bd2da4e81ab1ceff12ee7b8"), "128.241.93.211", 7218, arrival,
            0x0DE0B6B3A7E8421CUL);

        Assert.Equal(DungeonChannelHandOff.AnsChannelMoveBytes * 2, Retail2556.Length);
        Assert.Equal(Retail2556, Hex(body));
        Assert.Equal(3, row.Map_Level);
    }

    [Theory]
    [InlineData("02d06ec2-275dd078-00014104 06:14:29.525", 13u, (ushort)2, "8e7c06000000d642000096420000884264")] 
    [InlineData("d414c84e-29dccad7-00004a74 14:24:28.809", 9u, (ushort)3, "bf740600000008430000ca430000204100")]  
    [InlineData("d414c84e-2f393288-00003550 15:46:41.063", 42u, (ushort)3, "778006005cafb143d7a30e43000052425a")] 
    [InlineData("7448b82e-046fea8e-00009efc 12:13:43.732", 42u, (ushort)1, "758006005cafb143d7a30e43000052425a")] 
    public async Task DestinationBlock_IsTheOfferedRowsArrival_InEveryArchiveHandOff(string frame, uint group,
        ushort offeredLevel, string retailBlock)
    {
        var data = await DungeonDataAsync();
        var (_, arrival) = PartyDungeonTeleportHandler.ResolveArrival(data, group, offeredLevel)!.Value;

        var body = DungeonChannelHandOff.BuildAnsChannelMove2556(1, new byte[16], "127.0.0.1", 7201, arrival, 1);

        
        Assert.True(retailBlock == Hex(body.AsSpan(56, 17).ToArray()), frame);
    }

    [Fact]
    public async Task NoOffer_EntersTheGroupsLowestLevel_AndAnUnknownGroupHasNoArrival()
    {
        var data = await DungeonDataAsync();
        var lowest = data.DungeonInfo.Where(d => d.DungeonGroupID == 42).Min(d => d.Map_Level);

        var (row, _) = PartyDungeonTeleportHandler.ResolveArrival(data, 42, null)!.Value;
        Assert.Equal(lowest, row.Map_Level);
        
        Assert.Equal(lowest, PartyDungeonTeleportHandler.ResolveArrival(data, 42, 200)!.Value.Row.Map_Level);
        Assert.Null(PartyDungeonTeleportHandler.ResolveArrival(data, 250, 1));
    }

    [Fact]
    public void Prelude_IsRetailsTwoFrames()
    {
        
        Assert.Equal("010e0e00cfe311265208010011265208",
            Hex(PlayerSpawnService.BuildActorOutList(0x08522611u, new[] { 0x08522611u })));
    }

    [Fact]
    public void Builder_RejectsATokenOfTheWrongSize()
    {
        var arrival = new PositionData { Area_ID = 426103 };
        Assert.Throws<ArgumentException>(() =>
            DungeonChannelHandOff.BuildAnsChannelMove2556(1, new byte[15], "127.0.0.1", 7201, arrival, 1));
    }

 /// <summary>The three tables the arrival needs, copied from data/ into a private directory.</summary>
    private static async Task<GameDataStore> DungeonDataAsync()
    {
        var source = RequireDataDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "ro2-dungeon2556-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var table in new[] { "DungeonInfo.json", "Position_Info.json", "Map_List.json" })
                File.Copy(Path.Combine(source, table), Path.Combine(dir, table));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "DungeonInfo.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/DungeonInfo.json");
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
