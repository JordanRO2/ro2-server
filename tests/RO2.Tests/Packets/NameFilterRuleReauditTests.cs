using Microsoft.Extensions.Logging.Abstractions;
using NameFilterRow = RO2.GameData.Tables.NameFilter;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat.Handlers.Rmi;
using RO2.Server.Chat;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>RMI 1564 is answered with 2814 when the name contains the filtered row "GM".</summary>
public class NameFilterRuleReauditTests
{
    
    [Theory]
    
    [InlineData("iRO2GM", 15403u)]
    [InlineData("iRO2GM1", 15403u)]
    [InlineData("iRO2A", 0u)]
    [InlineData("iRO2Warrior", 0u)]
    public void RenameCheck_WithTheRealNameFilter_GivesRetailsAnswer(string newName, uint retail)
    {
        var (result, _) = CharacterRename.CheckName(newName, "lOMEGAl", LoadNameFilter());
        Assert.Equal(retail, result);
    }

    [Theory]
    [InlineData("Sigma", NameRefusal.None)]          
    [InlineData("iRO2Warrior", NameRefusal.None)]    
    [InlineData("PSFARMSTONE", NameRefusal.None)]    
    [InlineData("xGMx", NameRefusal.Prohibited)]
    [InlineData("admin", NameRefusal.Prohibited)]
    public void NameRule_WithTheRealNameFilter(string name, NameRefusal expected)
        => Assert.Equal(expected, NameRules.Check(name, LoadNameFilter()).refusal);

    [Fact]
    public void ContainedGmRule_NeedsTheNameFilterRow()
    {
        
        Assert.Equal(NameRefusal.None, NameRules.Check("iRO2GM", [new NameFilterRow { name = "RO2" }]).refusal);
        Assert.Contains(LoadNameFilter(), r => r.name == "GM");
    }

    
    private static IReadOnlyList<NameFilterRow> LoadNameFilter()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "name_filter.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var rows = JsonSerializer.Deserialize<List<NameFilterRow>>(File.ReadAllText(Path.Combine(dir!.FullName, "data", "name_filter.json")));
        Assert.NotNull(rows);
        return rows!;
    }
}
