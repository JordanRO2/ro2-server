using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
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

/// <summary>Tests the 2598 sent before a 1531 hand-off and the 2805 answer to a party random search (1556).</summary>
public class DungeonFinderPartyRoundReauditTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    
 /// <summary>2598 NotifyChangeP2PGroupID with no group: a single u32 of 0.</summary>
    [Fact]
    public void LeaveP2PGroup_IsRetails2598Body()
        => Assert.Equal("00000000", Hex(DungeonChannelHandOff.LeaveP2PGroup2598.ToArray()));

    
 /// <summary>
 /// A random search (1556) by a party member opens a round like a 1555, but the searcher's answer is sent
 /// as 2805 with a u16 result of 0. The members' frames (2822 and the List_ID 0 2806) are the same as for 1555.
 /// </summary>
    [Fact]
    public void RandomSearchRound_AnswersTheSearcherOn2805()
    {
        var row = FindDungeonRows().First(r => r.Category_ID == 2 && r.Party_Num == 5);
        var searcher = new PlayerState { SessionId = 20, Name = "leader" };
        var member = new PlayerState { SessionId = 21, Name = "member" };
        var round = new DungeonFinderPartyRound();

        var opened = round.Open(9, row, searcher, DungeonFinderPackets.RoleDealer, [member], (_, _, _) => true,
            ZoneS2COpcodes.AnsPartyMatchingFindDungeonRandom);

        Assert.Equal(DungeonFinderPartyRound.OpenStatus.Opened, opened.Status);
        var toSearcher = opened.Sends.Where(o => o.SessionId == 20).Select(o => (o.Opcode, Hex(o.Body))).ToArray();
        Assert.Equal((ZoneS2COpcodes.AnsPartyMatchingFindDungeonRandom, "0000"), toSearcher[0]);
        Assert.DoesNotContain(opened.Sends, o => o.Opcode == ZoneS2COpcodes.AnsPartyMatchingFindDungeon);
        var toMember = opened.Sends.Where(o => o.SessionId == 21).Select(o => o.Opcode).ToArray();
        Assert.Equal(new[] { ZoneS2COpcodes.NfyFindParty_Party_Dungeon_SelectRolePlease, ZoneS2COpcodes.AnsPartyMatchingInProgressInfo },
            toMember);
    }

 /// <summary>The 2805 refusal codes are the two bytes of the 2804 answer.</summary>
    [Theory]
    [InlineData(0, "0000")]
    [InlineData(9, "0900")]
    public void SearcherAnswer_IsTheResultU16OnEitherOpcode(int result, string hex)
    {
        Assert.Equal(hex, Hex(DungeonFinderPartyRound.SearcherAnswer(ZoneS2COpcodes.AnsPartyMatchingFindDungeonRandom, (ushort)result)));
        Assert.Equal(hex, Hex(DungeonFinderPartyRound.SearcherAnswer(ZoneS2COpcodes.AnsPartyMatchingFindDungeon, (ushort)result)));
    }

    
    private static List<FindDungeonList> FindDungeonRows()
        => JsonSerializer.Deserialize<List<FindDungeonList>>(
            File.ReadAllText(Path.Combine(RequireDataDirectory(), "Find_Dungeon_List.json")), JsonOptions)!;
}
