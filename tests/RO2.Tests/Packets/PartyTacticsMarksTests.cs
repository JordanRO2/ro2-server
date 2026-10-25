using RO2.GameData.Tables;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The party's tactics-mark list (<see cref="PartyTacticsMarks"/>) and the map-load 2751 AnsPartyTacticsTarget built
/// from it. Each test replays the announced marks in order and compares the resulting list with the one sent to
/// the member on map load.
/// </summary>
public class PartyTacticsMarksTests
{
    private static readonly DateTime T0 = new(2026, 8, 9, 21, 0, 0, DateTimeKind.Utc);

 /// <summary>The shipped Tactics_Target rows (data/Tactics_Target.json): 1-10 numbered icons, 11-17 pictures.</summary>
    private static readonly TacticsTarget[] ShippedRows = Enumerable.Range(1, 17).Select(i => new TacticsTarget
    {
        Tactics_List = (byte)i,
        Route = i <= 10 ? $"FX\\Common\\Tactic\\Tactic_icon_{i:00}.nif"
            : "FX\\Common\\Tactic\\Tactic_icon_" + new[] { "heart", "clover", "skeleton", "triangle", "star", "diamond", "dollar" }[i - 11] + ".nif",
    }).ToArray();

    private static void Set(PartyState party, ushort mark, uint handle, int second)
        => PartyTacticsMarks.Set(party, handle, mark, PartyTacticsMarks.IsNumbered(ShippedRows, mark), T0.AddSeconds(second));

    [Fact]
    public void NumberedMarks_AreTheNumberIcons()
    {
        for (ushort m = 1; m <= 10; m++) Assert.True(PartyTacticsMarks.IsNumbered(ShippedRows, m));
        for (ushort m = 11; m <= 17; m++) Assert.False(PartyTacticsMarks.IsNumbered(ShippedRows, m));
        Assert.False(PartyTacticsMarks.IsNumbered(ShippedRows, 0));
        Assert.False(PartyTacticsMarks.IsNumbered(null, 3));
    }

    [Fact]
    public async Task NumberedMarks_ReadFromTheShippedTable()
    {
        
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "data", "Tactics_Target.json")))
            root = root.Parent;
        if (root == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Tactics_Target.json");
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<TacticsTarget>>(
            await File.ReadAllBytesAsync(Path.Combine(root.FullName, "data", "Tactics_Target.json")))!;
        Assert.Equal(17, rows.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (ushort)i),
            rows.Select(r => (ushort)r.Tactics_List).Where(m => PartyTacticsMarks.IsNumbered(rows, m)).OrderBy(m => m));
    }

    [Fact]
    public void ANumberedMark_KeepsItsSlot_AndTheMapLoadListIsRetails()
    {
        
        var party = new PartyState { PartyId = 1 };
        Set(party, 1, 0x0081ee13, 34);     
        Set(party, 14, 0x0081ee13, 101);   
        Set(party, 2, 0x0081fb13, 110);    
        Set(party, 3, 0x00820513, 173);    
        Set(party, 13, 0x00820c13, 430);   
        Set(party, 2, 0x00821f13, 938);    
        Set(party, 2, 0x00826d13, 1537);   
        Set(party, 9, 0x00827813, 1686);   
        
        Assert.Equal("012b2b0045e4000000001d0c0000050e0013ee81000200136d82000300130582000d00130c8200090013788200",
            Convert.ToHexString(PartyTacticsMarks.BuildMapEntry2751(party, 3101, 0x1111)!).ToLowerInvariant());

        Set(party, 3, 0x00828713, 2006);   
        Set(party, 10, 0x0082c613, 2296);  
        Set(party, 3, 0x0082ec13, 2612);   
        
        Assert.Equal("0131310045e4000000001d0c0000060e0013ee81000200136d8200030013ec82000d00130c82000900137882000a0013c68200",
            Convert.ToHexString(PartyTacticsMarks.BuildMapEntry2751(party, 3101, 0x1111)!).ToLowerInvariant());
    }

    [Fact]
    public void APictureMark_IsNotUnique_AndARemarkedTargetGoesLast()
    {
        
        
        var party = new PartyState { PartyId = 2 };
        Set(party, 13, 0x032caa11, 0);
        Set(party, 15, 0x032bea11, 5);
        Set(party, 16, 0x032bb411, 18);
        Set(party, 14, 0x032d5511, 24);
        Set(party, 13, 0x032d8511, 34);    
        Set(party, 7, 0x032d8011, 50);
        Set(party, 10, 0x032d8511, 58);    
        Set(party, 11, 0x032ca911, 65);
        Set(party, 12, 0x032ca811, 74);
        Set(party, 4, 0x032b6c11, 96);
        Assert.Equal("0143430045e4000000001d0c0000090d0011aa2c030f0011ea2b03100011b42b030e0011552d03070011802d030a0011852d03"
                     + "0b0011a92c030c0011a82c030400116c2b03",
            Convert.ToHexString(PartyTacticsMarks.BuildMapEntry2751(party, 3101, 0x2222)!).ToLowerInvariant());
    }

    [Fact]
    public void AClearedMark_StaysOnTheList()
    {
        
        
        var party = new PartyState { PartyId = 3 };
        Set(party, 14, 0x0954cb11, 0);
        Set(party, 0, 0x0954cb11, 1346);
        Assert.Equal("0113130045e400000000c504000001000011cb5409",
            Convert.ToHexString(PartyTacticsMarks.BuildMapEntry2751(party, 1221, 0x3333)!).ToLowerInvariant());
    }

    [Fact]
    public void AfterAChannelMove_TheMoverGetsOnlyTheMarksSetSince()
    {
        
        
        var party = new PartyState { PartyId = 4 };
        const ulong mover = 0x0000_1234_0000_0001, stayer = 0x0000_1234_0000_0002;
        Set(party, 15, 0x0b00fc11, 0);
        PartyTacticsMarks.NoteChannelMove(mover, T0.AddSeconds(60));
        Assert.Null(PartyTacticsMarks.BuildMapEntry2751(party, 1100, mover));
        Assert.Equal("0113130045e4000000004c040000010f0011fc000b",
            Convert.ToHexString(PartyTacticsMarks.BuildMapEntry2751(party, 1100, stayer)!).ToLowerInvariant());

        
        
        var party2 = new PartyState { PartyId = 5 };
        const ulong dontFocusMe = 0x0000_1234_0000_0003;
        PartyTacticsMarks.NoteChannelMove(dontFocusMe, T0);
        Set(party2, 1, 0x0c521711, 57);
        Set(party2, 2, 0x0c54d111, 63);
        Assert.Equal("0119190045e400000000c50400000201001117520c020011d1540c",
            Convert.ToHexString(PartyTacticsMarks.BuildMapEntry2751(party2, 1221, dontFocusMe)!).ToLowerInvariant());
    }

    [Fact]
    public void APartyWithNoMarks_GetsNo2751()
    {
        Assert.Null(PartyTacticsMarks.BuildMapEntry2751(new PartyState { PartyId = 6 }, 1100, 0x4444));
    }
}
