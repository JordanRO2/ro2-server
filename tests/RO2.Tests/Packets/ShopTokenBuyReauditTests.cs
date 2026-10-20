using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Domain.GameData;
using RO2.Identity.Application.Commands;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Handlers.Rmi;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>Tests the 1334 Buy_Type 1 token buy (2379 reason 2, 2375 mode 1) and the token-take plan.</summary>
public sealed class ShopTokenBuyReauditTests
{
    private static readonly JsonSerializerOptions DataJson = new() { PropertyNameCaseInsensitive = true };

 /// <summary>The repo's data folder, found by walking up from the test binaries.</summary>
    private static string DataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "BuyTokenInfo.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/BuyTokenInfo.json");
    }

    
 /// <summary>The 2375 answer to a token buy of item 21330031 from Merchant row 2885, with BuyTokenInfo 1125 (item 60000121 x2) and count 1. A full 170-byte body.</summary>
    private const string Retail2375Token =
        "02a700a700c1e211eb870700000000b712915002000000000000000000000001016504006f784501000000001153a40da83d040e6f784501000001040d00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void TokenBuy2375_Mode1_IsRetailsFrame()
    {
        var retail = Convert.FromHexString(Retail2375Token);
        var record = retail[44..];
        Assert.Equal(126, record.Length);

        var ours = ShopItemPackets.BuildBuyItem2375(0x0787EB11u, 9_941_619_383L, 21330031u, new[] { record },
            ShopItemPackets.BuyModeToken, 1125);

        Assert.Equal(Retail2375Token, Hex(ours));
    }

    [Fact]
    public void TokenBuy2379_Reason2_MatchesRetailsHeader()
    {
        
        
        const string retailPrefix = "028a008a00c7e211eb8707000002002bad9f06a83d040e798793030000e7040c00000000000000000000000000000000";
        var prefix = Convert.FromHexString(retailPrefix);
        var record = new byte[126];
        prefix.AsSpan(15).CopyTo(record);

        var ours = ShopItemPackets.BuildDestroyItem2379(0x0787EB11u, record, ShopItemPackets.DestroyReasonTokenBuy);

        Assert.Equal(141, ours.Length);
        Assert.Equal(retailPrefix, Hex(ours[..48]));
        
        Assert.Equal(0, ShopItemPackets.BuildDestroyItem2379(0x0787EB11u, record)[13]);
    }

    private static async Task<BuyTokenInfo> TokenRowAsync(ushort id)
    {
        await using var fs = File.OpenRead(Path.Combine(DataDir(), "BuyTokenInfo.json"));
        var rows = (await JsonSerializer.DeserializeAsync<List<BuyTokenInfo>>(fs, DataJson))!;
        return rows.Single(r => r.ID == id);
    }

    [Fact]
    public async Task TokenRow1125_TakesTwoTokens_FromTheStack_AsRetailDid()
    {
        var row = await TokenRowAsync(1125);
        Assert.True(BuyItemHandler.IsTokenOnly(row));
        Assert.Equal((60000121u, (ushort)2), (row.TokenItemID1, row.NeedValue1));

        
        var stack = new InventoryItem { Id = 0x0E043DA8069FAD2BUL, ItemId = 60000121, Count = 233, Slot = 30, StorageType = 0 };
        var takes = BuyItemHandler.PlanTokenTake(new[] { stack }, row, quantity: 1);

        Assert.NotNull(takes);
        var (taken, left) = Assert.Single(takes);
        Assert.Same(stack, taken);
        Assert.Equal(231, left);
    }

    [Fact]
    public void TokenTake_SpillsOverStacksLowestCellFirst_AndRefusesWhenShort()
    {
        var row = new BuyTokenInfo { ID = 9, TokenItemID1 = 12005817, NeedValue1 = 100, TokenItemID2 = 40001183, NeedValue2 = 31 };
        var a = new InventoryItem { Id = 1, ItemId = 12005817, Count = 255, Slot = 12, StorageType = 0 };
        var b = new InventoryItem { Id = 2, ItemId = 12005817, Count = 255, Slot = 3, StorageType = 0 };
        var c = new InventoryItem { Id = 3, ItemId = 40001183, Count = 60, Slot = 5, StorageType = 0 };
        var d = new InventoryItem { Id = 4, ItemId = 40001183, Count = 60, Slot = 6, StorageType = 0 };
        var bank = new InventoryItem { Id = 5, ItemId = 12005817, Count = 255, Slot = 1, StorageType = 2 };
        var owned = new[] { a, b, c, d, bank };

        
        
        var takes = BuyItemHandler.PlanTokenTake(owned, row, quantity: 3);
        Assert.NotNull(takes);
        Assert.Equal(new[] { (2UL, 0), (1UL, 210), (3UL, 0), (4UL, 27) }, takes.Select(t => (t.Stack.Id, t.Left)));

        
        Assert.Null(BuyItemHandler.PlanTokenTake(owned, row, quantity: 6));
        Assert.Null(BuyItemHandler.PlanTokenTake(new[] { bank, c, d }, row, quantity: 1));
    }

    [Fact]
    public void TokenRowWithPointCosts_IsNotTokenOnly()
    {
        Assert.False(BuyItemHandler.IsTokenOnly(new BuyTokenInfo { ID = 1, TokenItemID1 = 1, NeedValue1 = 1, PvP_Point = 10 }));
        Assert.False(BuyItemHandler.IsTokenOnly(new BuyTokenInfo { ID = 1, Mastery_Point = 10 }));
        Assert.False(BuyItemHandler.IsTokenOnly(new BuyTokenInfo { ID = 1, Dia = 50 }));
        Assert.True(BuyItemHandler.IsTokenOnly(new BuyTokenInfo { ID = 1, TokenItemID1 = 1, NeedValue1 = 1 }));
    }
}
