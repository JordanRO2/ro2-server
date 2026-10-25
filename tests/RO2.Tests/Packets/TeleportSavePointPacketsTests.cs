using System.Buffers.Binary;
using System.Text.Json;
using RO2.GameData.Tables;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 3010 AnsTeleportSavePoint (<see cref="TeleportSavePointPackets"/>) and the /escape cooldown behind it
/// (<see cref="EscapeCooldown"/>).
/// </summary>
public class TeleportSavePointPacketsTests
{
 /// <summary>Six reference 3010 bodies and the whole seconds each carries.</summary>
    public static TheoryData<string, long> RetailFrames => new()
    {
        { "405f50555500f53e6e3f0000000000000000", 319 },  
        { "405fa8aaaa925f2c793f0000000000000000", 531 },  
        { "405faaaa4aa54439933f0000000000000000", 1622 }, 
        { "405fa0aaaa2cf9c5523f0000000000000000", 99 },   
        { "405f55554573f0c2a23f0000000000000000", 3166 }, 
        { "405f5455952dd8828d3f0000000000000000", 1245 }, 
    };

    [Theory]
    [MemberData(nameof(RetailFrames))]
    public void Build_MatchesRetailFrame(string hex, long seconds)
    {
        var retail = Convert.FromHexString(hex);
        var built = TeleportSavePointPackets.BuildAnsTeleportSavePoint3010(seconds);

        Assert.Equal(18, built.Length);
        
        Assert.Equal(retail[..2], built[..2]);
        Assert.Equal(retail[10..], built[10..]);

        
        
        double retailDays = BinaryPrimitives.ReadDoubleLittleEndian(retail.AsSpan(2));
        double builtDays = BinaryPrimitives.ReadDoubleLittleEndian(built.AsSpan(2));
        Assert.Equal(seconds, Math.Round(retailDays * 86400.0));
        Assert.InRange(Math.Abs(builtDays - retailDays) * 86400.0, 0.0, 1e-6);
        Assert.Equal(ClientMinutes(retailDays), ClientMinutes(builtDays));
    }

    [Fact]
    public void ClientMinutes_OfRetailFrames()
    {
        
        Assert.Equal(52, ClientMinutes(BinaryPrimitives.ReadDoubleLittleEndian(Convert.FromHexString("55554573f0c2a23f"))));
        Assert.Equal(1, ClientMinutes(BinaryPrimitives.ReadDoubleLittleEndian(Convert.FromHexString("a0aaaa2cf9c5523f"))));
    }

    [Fact]
    public void Length_IsConfigDef81InMinutes()
    {
        var config = JsonSerializer.Deserialize<List<ConfigDef>>(
            File.ReadAllBytes(Path.Combine(RequireDataDirectory(), "Config_Def.json")));
        Assert.NotNull(config);
        Assert.Equal(TimeSpan.FromMinutes(60), EscapeCooldown.Length(config));
    }

    [Fact]
    public void Length_NoRow_IsNoCooldown()
    {
        Assert.Equal(TimeSpan.Zero, EscapeCooldown.Length(Array.Empty<ConfigDef>()));
    }

    [Fact]
    public void Cooldown_CountsDownToOneExpiry_AsRetailsTwoRefusals()
    {
        
        
        const uint character = 0xE5CA0001;
        var hour = TimeSpan.FromMinutes(60);
        long escapedAt = 1_000_000;

        Assert.True(EscapeCooldown.TryClaim(character, escapedAt, hour, out _, out var none));
        Assert.Null(none);

        Assert.False(EscapeCooldown.TryClaim(character, escapedAt + 434, hour, out var first, out _));
        Assert.Equal(3166, first);
        
        Assert.False(EscapeCooldown.TryClaim(character, escapedAt + 434 + 1921, hour, out var second, out _));
        Assert.Equal(1245, second);

        
        Assert.True(EscapeCooldown.TryClaim(character, escapedAt + 3600, hour, out var left, out var previous));
        Assert.Equal(0, left);
        Assert.Equal(escapedAt, previous);
    }

    [Fact]
    public void Release_GivesBackAClaimWhoseWarpFailed()
    {
        const uint character = 0xE5CA0002;
        var hour = TimeSpan.FromMinutes(60);

        Assert.True(EscapeCooldown.TryClaim(character, 5_000, hour, out _, out var previous));
        EscapeCooldown.Release(character, 5_000, previous);
        
        Assert.True(EscapeCooldown.TryClaim(character, 5_001, hour, out var left, out _));
        Assert.Equal(0, left);
    }

 /// <summary>Mirrors the client's minute calculation for a positive span: truncation of (days + 0.5 seconds) * 1440.</summary>
    private static long ClientMinutes(double days) => (long)((days + 0.5 / 86400.0) * 1440.0);

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Config_Def.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "no ancestor of " + AppContext.BaseDirectory + " holds data/Config_Def.json");
    }
}
