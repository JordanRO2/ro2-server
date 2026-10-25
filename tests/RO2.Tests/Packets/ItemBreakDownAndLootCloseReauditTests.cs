using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData.Tables;
using RO2.GameData;
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

/// <summary>Break-down answer (2330) to 1328, and loot-close answer (2398) to 1339.</summary>
public sealed class ItemBreakDownAndLootCloseReauditTests
{
    
    [Fact]
    public void BreakDownOwn2330_IsRetailsFrame()
    {
        
        
        Assert.Equal("01141400a7e200000000112bee075c384201112bee07",
            Hex(LootNotifyPackets.BuildUseItemNotify2330(0x07EE2B11u, 21117020u)));
    }

    
    [Fact]
    public void LootClose2398_IsRetailsModalFrame()
        
        
        => Assert.Equal("011b1b00eae211e81b0711e81b07000000000000000200000000000000",
            Hex(NpcShopCloseHandler.BuildLootEndState2398(0x071BE811u)));
}
