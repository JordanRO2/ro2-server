using RO2.Server.Zone.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// NPC enter-combat tests: the in-combat flag of the 2398 reset and the 2729 combat line, including multi-row groups.
/// </summary>
public class NpcCombatLineReauditTests
{
    
    [Theory]
    
    [InlineData("011b1b00eae20000000013c5640903000100000000030000001108050f", 0x0964C513u, 0x0F050811u)]
    
    [InlineData("011b1b00eae20000000013f11d00030001000000000300000011b4020f", 0x001DF113u, 0x0F02B411u)]
    public void ChangeState2398_NpcEnterCombatIsRetailsFrameWithTheInCombatFlag(string hex, uint npc, uint pc)
    {
        
        
        Assert.Equal(Convert.FromHexString(hex), CombatPackets.BuildChangeState2398(npc,
            CombatPackets.ActorState.NpcEnterCombat, targetHandle: pc, trailing: 3, combatFlag: 1));
    }

    
    private static string DataFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName, "data" }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + string.Join('/', parts));
    }

    private static HashSet<uint> ReadIds(string file, string property)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DataFile(file)));
        return doc.RootElement.EnumerateArray().Select(r => r.GetProperty(property).GetUInt32()).ToHashSet();
    }

    [Fact]
    public void EngageLines_ShippedTableLoadsAndEveryRowIsGameData()
    {
        var table = NpcEngageLines.Load(DataFile("npc", "NpcEngageLines.json"));
        Assert.Equal(34, table.Count);
        var dlg = ReadIds("Npc_Dlg.json", "Text_StringID");
        var npcs = ReadIds("NPCInfo.json", "ID");
        Assert.All(table.Rows, r =>
        {
            Assert.Contains(r.LineId, dlg);
            Assert.Contains(r.Asset, npcs);
            Assert.NotEqual(0u, r.Asset % 10000);
            Assert.InRange(r.Spoken, 1, r.Engagements);
        });
        
        Assert.True(table.TryGet(50003746, out var row));
        Assert.Equal(500374612u, row.LineId);
        Assert.Equal(130.0 / 158.0, row.Chance, 12);
    }

    [Fact]
    public void CombatLine_VariantSpeaksItsMeasuredLineAtItsMeasuredRate()
    {
        var table = NpcEngageLines.Load(DataFile("npc", "NpcEngageLines.json"));
        var index = NpcChatService.BuildIndex([]);
        double chance = 130.0 / 158.0;
        Assert.True(NpcChatService.TryPickCombatLine(50003746, true, index, table, chance - 1e-9, 0, out var line));
        Assert.Equal(500374612u, line);
        Assert.False(NpcChatService.TryPickCombatLine(50003746, true, index, table, chance, 0, out _));
        
        Assert.False(NpcChatService.TryPickCombatLine(50003731, true, index, table, 0, 0, out _));
        Assert.False(NpcChatService.TryPickCombatLine(50003746, true, index, null, 0, 0, out _));
        Assert.False(NpcChatService.TryPickCombatLine(50003746, false, index, table, 0, 0, out _));
    }

    [Fact]
    public void CombatLine_BaseTemplateIsACoinFlip()
    {
        
        Assert.Equal(0.5, NpcChatService.BaseCombatLineChance);
        var index = NpcChatService.BuildIndex([40010201u, 40010101u]);
        Assert.True(NpcChatService.TryPickCombatLine(40010000, true, index, null, 0.4999, 0.9, out var line));
        Assert.Equal(40010201u, line);
        Assert.False(NpcChatService.TryPickCombatLine(40010000, true, index, null, 0.5, 0.9, out _));
        Assert.False(NpcChatService.TryPickCombatLine(40020000, true, index, null, 0, 0, out _));   
    }

    [Fact]
    public void Index_KeepsMultiRowGroupsAndDropsTheScriptedTemplates()
    {
        var index = NpcChatService.BuildIndex([51100202u, 51100201u, 51100101u, 51100102u, 13900103u, 13900101u,
            13900102u, 93150111u, 93150154u, 93140101u, 51100301u]);
        Assert.Equal(new uint[] { 51100101, 51100102 }, index.Idle[5110]);
        Assert.Equal(new uint[] { 51100201, 51100202 }, index.Combat[5110]);
        Assert.Equal(new uint[] { 13900101, 13900102, 13900103 }, index.Idle[1390]);
        Assert.False(index.Idle.ContainsKey(9315));
        Assert.False(index.Idle.ContainsKey(9314));
        Assert.Equal(2, index.Idle.Count);       

        
        Assert.Equal(51100201u, NpcChatService.PickRow(index.Combat[5110], 0.0));
        Assert.Equal(51100201u, NpcChatService.PickRow(index.Combat[5110], 0.4999));
        Assert.Equal(51100202u, NpcChatService.PickRow(index.Combat[5110], 0.5));
        Assert.Equal(51100202u, NpcChatService.PickRow(index.Combat[5110], 0.999999));
        Assert.True(NpcChatService.TryPickCombatLine(51100000, true, index, null, 0.1, 0.75, out var line));
        Assert.Equal(51100202u, line);
    }
}
