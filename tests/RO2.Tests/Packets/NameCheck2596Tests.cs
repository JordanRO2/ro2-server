using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using NameFilterRow = RO2.GameData.Tables.NameFilter;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 1444 ReqCheckPCName and 2596 OnCheckPCName: the response body matches the reference byte for byte, and each refusal uses
/// the code the client maps to the correct text (7: prohibited word; 10: name already in use).
/// </summary>
public class NameCheck2596Tests
{
    
    private static readonly IReadOnlyList<NameFilterRow> Filter =
    [
        new() { name = "admin" }, new() { name = "ＦＵＣＫ" }, new() { name = "GameMaster" }, new() { name = "RM" },
        new() { name = "GM" },
    ];

    [Theory]
    
    [InlineData(0x004D3489u, (ushort)7, "JMDGFBZ", "1700000089344d00070007004a004d0044004700460042005a0000")]
    
    [InlineData(0x004D3489u, (ushort)0, "fcyr1", "1300000089344d00000005006600630079007200310000")]
    
    [InlineData(0x0042220Du, (ushort)10, "Lool3", "130000000d2242000a0005004c006f006f006c00330000")]
    public void Body_MatchesRetail(uint account, ushort result, string name, string retailHex)
    {
        var body = NameCheckHandler.BuildOnCheckPCName2596(account, result, name);
        Assert.Equal(retailHex, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Theory]
    [InlineData("fs", NameRefusal.Format)]            
    [InlineData("bad name", NameRefusal.Format)]
    [InlineData("admin", NameRefusal.Prohibited)]
    [InlineData("ADMIN", NameRefusal.Prohibited)]
    [InlineData("fuck", NameRefusal.Prohibited)]      
    [InlineData("gamemaster", NameRefusal.Prohibited)]
    [InlineData("PSFARMSTONE", NameRefusal.None)]     
    [InlineData("irmyudnth", NameRefusal.None)]       
    [InlineData("Sigma", NameRefusal.None)]           
    [InlineData("xGMx", NameRefusal.Prohibited)]      
    [InlineData("GM", NameRefusal.Format)]            
    [InlineData("Assassin", NameRefusal.None)]        
    public void Check_ClassifiesTheRefusal(string name, NameRefusal expected)
        => Assert.Equal(expected, NameValidator.Check(name, Filter).refusal);

    [Fact]
    public void Refusals_MapToTheClientCodes()
    {
        Assert.Equal(0, NameCheckHandler.ResultFor(NameRefusal.None));
        Assert.Equal(7, NameCheckHandler.ResultFor(NameRefusal.Prohibited));
        Assert.Equal(10, NameCheckHandler.ResultFor(NameRefusal.Format));
    }
}
