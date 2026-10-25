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

/// <summary>Tests 2816 state 3 (dungeon finder reward mailed) and the one-mail-per-stack reward split.</summary>
public sealed class DungeonFinderRewardReauditTests
{
    
    [Fact]
    public void CompleteGift2816_State3_PinsTheReaderLayout()
    {
        
        
        Assert.Equal(3, DungeonFinderPackets.CompleteGiftMailed);
        Assert.Equal("1500000003", Hex(DungeonFinderPackets.BuildCompleteGift2816(21, DungeonFinderPackets.CompleteGiftMailed)));
    }

    [Fact]
    public void FinderRewardMail_IsOneMailPerStack_WithTheClientStringIds()
    {
        Assert.Equal(new[] { 5 }, DungeonFinderReward.SplitIntoMails(5, 99));
        Assert.Equal(new[] { 99, 99, 52 }, DungeonFinderReward.SplitIntoMails(250, 99));
        Assert.Equal(new[] { 1, 1 }, DungeonFinderReward.SplitIntoMails(2, 1));
        Assert.Empty(DungeonFinderReward.SplitIntoMails(0, 99));
        
        
        Assert.Equal(("11809", "11812", "11813"),
            (DungeonFinderReward.MailSender, DungeonFinderReward.MailTitle, DungeonFinderReward.MailBody));
    }
}
