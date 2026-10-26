using RO2.Network.Packets;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for RMI 1705 ReqAppearanceChange and its answer 3046 AnsAppearanceChange (<see cref="AppearanceChangePackets"/>).
/// Covers the success answer, the ticket update (2329), the no-ticket refusal and the gender-lock refusal.
/// </summary>
public class AppearanceChange1705Tests
{
 /// <summary>The requester's actor handle (3046 blob+8, 2329 blob+4; PC tag 0x11).</summary>
    private const uint Handle = 0x04B02D11;

 /// <summary>The 1705 request body: the create[0..14] block.</summary>
    private static readonly byte[] Request = Convert.FromHexString("0202020bff0007000000010fff0001");

    [Fact]
    public void Answer3046_ReproducesTheDumpFrame()
    {
        var expected = Convert.FromHexString("011d1d009ee400000000112db0040202020bff0007000000010fff00010000");
        var body = AppearanceChangePackets.BuildAnsAppearanceChange3046(Handle, Request, AppearanceChangePackets.ResultApplied);
        Assert.Equal(expected, body);
        Assert.Equal(AppearanceChangePackets.AnswerBodyLength, body.Length);
    }

    [Fact]
    public void TicketUse2329_ReproducesTheDumpFrame()
    {
        
        var expected = Convert.FromHexString("01161600a6e2112db00400000d029645030fa83d040e0003");
        var body = CombatPackets.BuildUseItem2329(Handle, 0x0D, 2, 0x0E043DA80F034596, 0,
            useSource: AppearanceChangePackets.UseSourceAppearanceTicket);
        Assert.Equal(expected, body);
    }

    [Theory]
    [InlineData(AppearanceChangePackets.ResultNoTicket)]
    [InlineData(AppearanceChangePackets.ResultGenderSpecificItemWorn)]
    public void Refusal3046_CarriesTheResultWhereTheReaderTakesIt(ushort result)
    {
        
        
        var body = AppearanceChangePackets.BuildAnsAppearanceChange3046(Handle, Request, result);
        Assert.Equal(31, body.Length);
        Assert.Equal(Convert.FromHexString("011d1d009ee400000000112db004"), body[..14]);
        Assert.Equal(Request, body[14..29]);
        Assert.Equal(result, (ushort)(body[29] | body[30] << 8));
    }

    [Fact]
    public void Refusal3046_ReproducesTheLiveNoTicketFrame()
    {
        
        
        var request = Convert.FromHexString("0201020aff0001000000010aff0001");
        var stored = Convert.FromHexString("0201010aff0001000000010aff0001" + "0300000010000400"); 
        var body = AppearanceChangePackets.BuildAnsAppearanceChange3046(0x0C457D11,
            AppearanceChangePackets.RefusalBlock(stored, request), AppearanceChangePackets.ResultNoTicket);
        Assert.Equal(Convert.FromHexString("011d1d009ee400000000117d450c0201010aff0001000000010aff0001145a"), body);
    }

    [Fact]
    public void RefusalBlock_WithoutAStoredBlock_IsTheRequest()
    {
        Assert.Equal(Request, AppearanceChangePackets.RefusalBlock(null, Request));
        Assert.Equal(Request, AppearanceChangePackets.RefusalBlock(new byte[14], Request));
    }

    [Fact]
    public void ApplyToCreateBlock_KeepsCreate0AndTheTailOfTheStoredBlock()
    {
        
        var stored = new byte[23];
        stored[0] = 1; stored[1] = 1; stored[15] = 3; stored[19] = 16; stored[21] = 4;
        var request = (byte[])Request.Clone();   

        var raw = AppearanceChangePackets.ApplyToCreateBlock(stored, storedCreate0: 1, request);

        Assert.Equal(23, raw.Length);
        Assert.Equal(1, raw[0]);
        Assert.Equal(request[1..], raw[1..15]);
        Assert.Equal(stored[15..], raw[15..]);
    }

    [Fact]
    public void ApplyToCreateBlock_WithoutAStoredBlock_TakesTheRequest()
    {
        Assert.Equal(Request, AppearanceChangePackets.ApplyToCreateBlock(null, storedCreate0: 0, Request));
        var withGender = AppearanceChangePackets.ApplyToCreateBlock(null, storedCreate0: 1, Request);
        Assert.Equal(1, withGender[0]);
        Assert.Equal(Request[1..], withGender[1..]);
    }

    [Fact]
    public void ReDerivedAppearance_TakesTheRequestedFields()
    {
        var stored = new byte[23];
        stored[0] = 2; stored[19] = 1;
        var raw = AppearanceChangePackets.ApplyToCreateBlock(stored, storedCreate0: 2, Request);
        var appearance = CharacterSerializer.ParseCreateAppearance(new PacketReader(raw));
        Assert.Equal(Request[1], appearance.Race);       
        Assert.Equal(Request[6], appearance.Face);       
        Assert.Equal(Request[14], appearance.HairStyle); 
        Assert.Equal(1, appearance.EyeColor);            
        Assert.Equal(raw, appearance.RawBytes);
    }

    [Fact]
    public void TicketItemType_IsExactlyTheSixAppearanceChangeTickets()
    {
        
        
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "data", "ItemInfo.json")))
            root = root.Parent;
        if (root == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/ItemInfo.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "data", "ItemInfo.json")));
        var tickets = doc.RootElement.EnumerateArray()
            .Where(r => r.GetProperty("Item_Type").GetInt32() == AppearanceChangePackets.AppearanceTicketItemType)
            .Select(r => r.GetProperty("ID").GetUInt32())
            .OrderBy(id => id)
            .ToArray();
        Assert.Equal(new uint[] { 60002002, 60002003, 60002004, 60002005, 60002006, 60002007 }, tickets);
    }

    [Theory]
    [InlineData(1, 1, new byte[] { 1 }, false)]    
    [InlineData(1, 2, new byte[] { 1 }, true)]     
    [InlineData(2, 1, new byte[] { 2, 3 }, true)]  
    [InlineData(1, 2, new byte[] { 0, 3 }, false)] 
    [InlineData(1, 2, new byte[] { 2 }, false)]    
    [InlineData(1, 2, new byte[0], false)]         
    public void GenderChange_IsBlockedOnlyByAWornItemOfTheOtherGender(byte oldGender, byte newGender, byte[] worn, bool blocked)
    {
        Assert.Equal(blocked, AppearanceRules.GenderChangeBlockedByWornItems(oldGender, newGender, worn));
    }
}
