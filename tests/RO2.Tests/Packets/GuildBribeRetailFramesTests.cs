using System.Text.Json;
using RO2.Server.Zone.Services;
using Xunit;
using EffectRow = RO2.GameData.Tables.EffectTable;

namespace RO2.Tests.Packets;

public class GuildBribeRetailFramesTests
{
 /// <summary>The repo's data/ folder, found by walking up from the test binaries (works on any machine and in CI).</summary>
    private static string DataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Effect_Table.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Effect_Table.json");
    }

    private static async Task<List<EffectRow>> EffectTableAsync()
    {
        await using var fs = File.OpenRead(Path.Combine(DataDir(), "Effect_Table.json"));
        return (await JsonSerializer.DeserializeAsync<List<EffectRow>>(fs))!;
    }

    
    private const string Ref2763 = "028400840062e41146740b610001f80000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001146740b0ab8207c76564c9d4a8de17b8381f9d5af";
    private const string Ref2765 = "010c0c0064e41146740b1146740b";

    [Fact]
    public void Sign2763IsByteExact()
    {
        var name = new byte[102];
        new byte[] { 0x61, 0x00, 0x01, 0xF8 }.CopyTo(name, 0);
        var guid = Convert.FromHexString("b8207c76564c9d4a8de17b8381f9d5af");
        var body = CloseRangeChatPackets.BuildNfyOpenCloseRangeChatRoom2763(0x0B744611u, name, 10, guid);
        Assert.Equal(135, body.Length);
        Assert.Equal(Ref2763, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void Exit2765IsByteExact()
        => Assert.Equal(Ref2765, Convert.ToHexString(CloseRangeChatPackets.BuildNfyExitCloseRangeChatRoom2765(0x0B744611u)).ToLowerInvariant());

    [Fact]
    public void Bribe2725IsHalfThePrize()
    {
        Assert.Equal(211_508UL, GuildBribePackets.HuntingBribeAmount(423_017, 50));
        Assert.Equal("343a030000000000", Convert.ToHexString(GuildBribePackets.BuildNfyGuildBribeHunting(211_508)).ToLowerInvariant());
        Assert.Equal(0UL, GuildBribePackets.HuntingBribeAmount(423_017, 0));
    }

    [Fact]
    public void Handover2730IsTheMastersTotal()
    {
        Assert.Equal("5ba8331b00000000", Convert.ToHexString(GuildHandoverPackets.BuildNfyHandoverMemberBribePointToMaster(215_240 + 456_155_027)).ToLowerInvariant());
        Assert.Equal("0000000000000000", Convert.ToHexString(GuildHandoverPackets.BuildNfyHandoverMemberBribePointToMaster(0)).ToLowerInvariant());
    }

    [Fact]
    public async Task TickPlanFromData()
    {
        var t = await EffectTableAsync();
        Assert.Equal(new EffectTickPlan(100, 20, 2000), EffectTickPlan.For(t, 105210, 1, 1, 10_000, 2_000));
        Assert.Equal(new EffectTickPlan(200, 40, 2000), EffectTickPlan.For(t, 105211, 1, 1, 10_000, 2_000));
        Assert.Equal(new EffectTickPlan(0, 2146, 3000), EffectTickPlan.For(t, 60035, 5, 1, 50_000, 14_310));
        Assert.Equal(new EffectTickPlan(0, -2674, 2000), EffectTickPlan.For(t, 50509, 1, 1, 50_000, 26_741));
        Assert.Equal(new EffectTickPlan(2205, 0, 10000), EffectTickPlan.For(t, 210046, 1, 1, 110_282, 5_000));
        Assert.Null(EffectTickPlan.For(t, 70190, 16492, 1, 50_000, 5_000));   
        Assert.Null(EffectTickPlan.For(t, 6319, 6632, 2, 50_000, 5_000));     
        Assert.Null(EffectTickPlan.For(t, 105202, 1, 1, 50_000, 5_000));     
        Assert.Null(EffectTickPlan.For(t, 3314, 10000, 1, 50_000, 5_000));   
    }

    [Fact]
    public void ItemDurationsAreFloat32()
    {
        Assert.Equal(86_400_104UL, EffectService.ItemEffectDurationMs(86400.1f));
        Assert.Equal(604_800_128UL, EffectService.ItemEffectDurationMs(604800.1f));
        Assert.Equal(259_200_096UL, EffectService.ItemEffectDurationMs(259200.1f));
        Assert.Equal(3_600_100UL, EffectService.ItemEffectDurationMs(3600.1f));
        Assert.Equal(1_800_100UL, EffectService.ItemEffectDurationMs(1800.1f));
        Assert.Equal(10_100UL, EffectService.ItemEffectDurationMs(10.1000003814697f));
    }

    [Fact]
    public void RegistryResolvesOwnerOnce()
    {
        var guid = Guid.NewGuid().ToByteArray();
        CloseRangeChatRooms.Register(new CloseRangeRoom(CloseRangeChatRooms.IdOf(guid), 777, 0x0B744611u, 10));
        Assert.True(CloseRangeChatRooms.ResolveExit(guid, 778) is CloseRangeMemberExited { CharacterId: 778 });
        Assert.True(CloseRangeChatRooms.ResolveExit(guid, 777) is CloseRangeOwnerExited { Room.OwnerHandle: 0x0B744611u });
        Assert.True(CloseRangeChatRooms.ResolveExit(guid, 777) is CloseRangeUnknownRoom);
    }
}
