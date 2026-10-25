using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>Tests the 2336 head (talent points, level, wallet) and the 2538 craft scroll resolved from a blueprint.</summary>
public class RepairAndCraftScrollReauditTests
{
    
 /// <summary>The 2336 head holds the remaining talent points at head+142 (body+152), not 0. A level-50 character with no talent skill learned has 49. Its level is at head+69 and its wallet at head+112, and the body length is 217 + 126 n.</summary>
    [Fact]
    public void RepairItem2336_HeadCarriesLiveTalentPointsLevelAndWallet()
    {
        var player = new PlayerState { EntityId = 0x0B2C1A11, CharacterUid = 0x1234, Level = 50, Gold = 0x01020304,
            Hp = 900, Mp = 300, Character = new RO2.Characters.Domain.Character { Name = "Repair", Level = 12 } };
        var body = RepairItemPackets.BuildOnRepairItem2336(player, [new byte[126]], new GameDataStore());

        Assert.Equal(217 + 126, body.Length);
        Assert.Equal((uint)(body.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));   
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)));
        var head = body.AsSpan(10, 205);
        Assert.Equal(50, head[69]);                                                  
        Assert.Equal(49, head[142]);                                                 
        Assert.Equal(0x01020304UL, BinaryPrimitives.ReadUInt64LittleEndian(head[112..]));
        Assert.Equal(900u, BinaryPrimitives.ReadUInt32LittleEndian(head[81..]));     
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(215)));  
        Assert.Equal(50, player.Character!.Level);                                   
    }

    
 /// <summary>Maps CraftScroll.ID to Craft_ID. Blueprints 644 and 645 teach the ids that the two 2538 answers add. An item with no CraftScroll row is not a blueprint.</summary>
    [Fact]
    public async Task CraftScroll_ResolvesTheBlueprintsRecipe()
    {
        var store = await LoadStoreAsync("CraftScroll.json", "Craft_ItemList.json");
        Assert.Equal((ushort)644, CraftScrollLearning.TryResolve(store, 22120059)!.ID);
        Assert.Equal((ushort)645, CraftScrollLearning.TryResolve(store, 22120060)!.ID);
        Assert.Null(CraftScrollLearning.TryResolve(store, 70000074));   
    }

 /// <summary>Blueprint answers: a 2538 lists the taught craft ids ([644], then [644, 645]). Each is followed in the same millisecond by a 2435 with the balance unchanged, and then by the 2329.</summary>
    [Fact]
    public void CraftScroll_AnswerFrames_AreTheDumps()
    {
        Assert.Equal("06000000000001008402", Hex(CraftRecipeStore.BuildAckCraftLearn2538([644])));
        Assert.Equal("080000000000020084028502", Hex(CraftRecipeStore.BuildAckCraftLearn2538([644, 645])));
        var player = new PlayerState { EntityId = 0x052A7811, Gold = 0x2A7C };
        Assert.Equal("01141400c6e311782a057c2a00000000000000000000", Hex(ActorVitalsPackets.ApplyZenyDeltaAndBuild(player, 0)));
        Assert.Equal(0x2A7C, player.Gold);
    }
}
