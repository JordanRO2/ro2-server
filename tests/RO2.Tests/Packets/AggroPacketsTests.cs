using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Byte-layout tests for the monster aggro notifies 2630 NfyAggroChanged and 2629 AnsAggroList.</summary>
/// <remarks>
/// These opcodes have no compact-length envelope. The first four bytes are the monster handle (low byte 0x13),
/// not a {1,2,4,8} marker. The layout consumes each body exactly.
/// </remarks>
public class AggroPacketsTests
{
    
    private const uint Monster = 16977171;         

    
    [Fact]
    public void AggroChanged2630_ReproducesARealRetailFrame()
    {
        
        
        var expected = Convert.FromHexString(
            "130d030111570f07010101114b0f070201074300610072006c00330065006500c4140e00");
        var body = AggroPackets.BuildAggroChanged2630(
            monsterHandle: Monster,
            currentTargetHandle: 118445841,
            pcHandle: 118442769,
            pcName: "Carl3ee",
            threat: 922820,
            changeFlag: 1);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void AggroChanged2630_IsBareAndCarriesTaggedHandles()
    {
        var body = AggroPackets.BuildAggroChanged2630(Monster, 118445841, 118442769, "Carl3ee", 922820);

        
        Assert.Equal(0x13, body[0]);
        Assert.Equal(Monster, BinaryPrimitives.ReadUInt32LittleEndian(body));
        
        Assert.Equal(118445841u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(0x11, body[4]);
        
        Assert.Equal(1, body[8]);
        Assert.Equal(1, body[9]);
        
        Assert.Equal(22 + 2 * "Carl3ee".Length, body.Length);
    }

    [Fact]
    public void AggroChanged2630_LengthTracksNameLength()
    {
        
        foreach (var name in new[] { "A", "Kuumo", "LarisaVonCheva" })
        {
            var body = AggroPackets.BuildAggroChanged2630(Monster, 0, 118442769, name, 1);
            Assert.Equal(22 + 2 * name.Length, body.Length);
            
            
            Assert.Equal(2, body[15]);                        
            Assert.Equal(1, body[16]);                        
            Assert.Equal(name.Length, body[17]);              
        }
    }

    
    [Fact]
    public void AggroList2629_ReproducesTheEmptyListFrame()
    {
        
        var expected = Convert.FromHexString("130b0301000000000100");
        var body = AggroPackets.BuildAggroList2629(
            monsterHandle: 16976659,
            currentTargetHandle: 0,
            entries: Array.Empty<AggroPackets.AggroEntry>());
        Assert.Equal(expected, body);
    }

    [Fact]
    public void AggroList2629_ReproducesARealNineEntryFrame()
    {
        
        
        
        var expected = Convert.FromHexString(
            "130d030111570f0701090111570f0702010953006c006500650071004900650061007200094048000111550f07" +
            "020107780078004c006f006b0069006500d85a140001114b0f070201074300610072006c00330065006500bc83" +
            "0b000111160f070201054b00750075006d006f009cf1030001112e0f070201084b006100720061004c00680079" +
            "006e0072ea00000111520f070201084e0061006d0061007a0075006b006f00030000000111270f070201066f00" +
            "410052004500530053000100000001111c0f0702010e4c006100720069007300610056006f006e004300680065" +
            "0076006100010000000111 3a0f0702010441007a007500760001000000".Replace(" ", ""));
        var entries = new[]
        {
            new AggroPackets.AggroEntry(118445841, "SleeqIear", 4734985),
            new AggroPackets.AggroEntry(118445329, "xxLokie", 1333976),
            new AggroPackets.AggroEntry(118442769, "Carl3ee", 754620),
            new AggroPackets.AggroEntry(118429201, "Kuumo", 258460),
            new AggroPackets.AggroEntry(118435345, "KaraLhyn", 60018),
            new AggroPackets.AggroEntry(118444561, "Namazuko", 3),
            new AggroPackets.AggroEntry(118433553, "oARESS", 1),
            new AggroPackets.AggroEntry(118430737, "LarisaVonCheva", 1),
            new AggroPackets.AggroEntry(118438417, "Azuv", 1),
        };
        var explicitBody = AggroPackets.BuildAggroList2629(Monster, 118445841, entries);
        Assert.Equal(expected, explicitBody);

        
        var autoBody = AggroPackets.BuildAggroList2629(Monster, entries);
        Assert.Equal(expected, autoBody);
    }

    [Fact]
    public void AggroList2629_TopThreatTargetPicksHighestThreatOrZero()
    {
        Assert.Equal(0u, AggroPackets.TopThreatTarget(Array.Empty<AggroPackets.AggroEntry>()));
        var entries = new[]
        {
            new AggroPackets.AggroEntry(0x070F0011u, "low", 10),
            new AggroPackets.AggroEntry(0x070F0111u, "high", 999),
            new AggroPackets.AggroEntry(0x070F0211u, "mid", 500),
        };
        Assert.Equal(0x070F0111u, AggroPackets.TopThreatTarget(entries));
    }

    [Fact]
    public void AggroList2629_MultiEntryDecodesBackToTheSameRows()
    {
        
        var entries = new[]
        {
            new AggroPackets.AggroEntry(0x070F5711u, "SleeqIear", 4734985),
            new AggroPackets.AggroEntry(0x070F4B11u, "Carl3ee", 754620),
        };
        var body = AggroPackets.BuildAggroList2629(Monster, 0x070F5711u, entries);

        var span = body.AsSpan();
        Assert.Equal(Monster, BinaryPrimitives.ReadUInt32LittleEndian(span));
        Assert.Equal(0x070F5711u, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4)));
        var o = 8;
        Assert.Equal(1, span[o]);            
        int count = span[o + 1];
        o += 2;
        Assert.Equal(2, count);
        foreach (var e in entries)
        {
            Assert.Equal(e.Flag, span[o]); o += 1;
            Assert.Equal(e.PcHandle, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(o))); o += 4;
            Assert.Equal(2, span[o]); o += 1;             
            Assert.Equal(1, span[o]); o += 1;             
            int nch = span[o]; o += 1;
            Assert.Equal(e.PcName.Length, nch);
            var name = System.Text.Encoding.Unicode.GetString(span.Slice(o, nch * 2)); o += nch * 2;
            Assert.Equal(e.PcName, name);
            Assert.Equal(e.Threat, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(o))); o += 4;
        }
        Assert.Equal(body.Length, o);
    }
}
