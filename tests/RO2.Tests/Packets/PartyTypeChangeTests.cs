using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1497 ReqPartyTypeChange (PartyService.ChangeTypeAsync, PartyTypeChangeHandler). An accepted change answers the leader
/// with 2691 and the other members with 2692, then sends the 2361 roster to everyone: both consumers clear the party frame
/// and only a roster refills it. A leader on a dungeon map is refused with 2691 code 15719 and nothing else.
/// </summary>
public class PartyTypeChangeTests
{
    [Fact]
    public void AnsPartyTypeChange_2691_MatchesRetailFrames()
    {
        
        
        Assert.Equal("000002", Convert.ToHexString(PartyStatePackets.BuildAnsPartyTypeChange(2)).ToLowerInvariant());
        Assert.Equal("000002", Convert.ToHexString(PartyStatePackets.BuildAnsPartyTypeChange(2, 0)).ToLowerInvariant());
        Assert.Equal("673d02", Convert.ToHexString(PartyStatePackets.BuildAnsPartyTypeChange(2, 15719)).ToLowerInvariant());
    }

 /// The accepted change through the handler: the leader gets 2691 and then the roster; the other online member gets
 /// 2692 and then the same roster, whose header carries type 2 at payload+8. A map the store does not hold is not a dungeon map.
    [Fact]
    public async Task AcceptedChange_AnswersNotifiesAndRefillsTheRoster()
    {
        await using var rig = await PartyWireRig.StartAsync(3);
        var leader = rig.AddPlayer(0, "Leader", mapId: 1200);
        var member = rig.AddPlayer(1, "Member", mapId: 1200);
        rig.AddPlayer(2, "Outsider", mapId: 1200);
        var party = rig.TrackParty(0x71, [leader, member]);
        await rig.DrainAsync();

        await NewHandler(rig).HandleAsync(rig.Sessions[0], new byte[] { 0x02 });

        Assert.Equal(2, party.PartyType);
        var roster = rig.Party.BuildRosterBody(party);
        Assert.Equal(2, roster[15]);
        Assert.Equal(new[]
        {
            PartyWireRig.Frame(ZoneS2COpcodes.AnsPartyTypeChange, [0x00, 0x00, 0x02]),
            PartyWireRig.Frame(ZoneS2COpcodes.OnPartyNotifyUpdate, roster),
        }, await rig.FramesAsync(0));
        Assert.Equal(new[]
        {
            PartyWireRig.Frame(ZoneS2COpcodes.NfyPartyTypeChange, Convert.FromHexString("0109090044e40000000002")),
            PartyWireRig.Frame(ZoneS2COpcodes.OnPartyNotifyUpdate, roster),
        }, await rig.FramesAsync(1));
        Assert.Empty(await rig.FramesAsync(2));
    }

 /// A full party change: each of the four other members receives one 2692 followed by the roster carrying type 2,
 /// as the leader does. The roster is sent immediately rather than on a periodic cadence.
    [Fact]
    public async Task AcceptedChange_InAFivePersonParty_NotifiesEachOtherMemberOnce()
    {
        await using var rig = await PartyWireRig.StartAsync(5);
        var members = Enumerable.Range(0, 5).Select(i => rig.AddPlayer(i, "Member" + i, mapId: 1100)).ToList();
        var party = rig.TrackParty(0x73, members);
        await rig.DrainAsync();

        await NewHandler(rig).HandleAsync(rig.Sessions[0], new byte[] { 0x02 });

        var roster = rig.Party.BuildRosterBody(party);
        Assert.Equal(2, roster[15]);
        Assert.Equal(new[]
        {
            PartyWireRig.Frame(ZoneS2COpcodes.AnsPartyTypeChange, Convert.FromHexString("000002")),
            PartyWireRig.Frame(ZoneS2COpcodes.OnPartyNotifyUpdate, roster),
        }, await rig.FramesAsync(0));
        for (int i = 1; i < 5; i++)
            Assert.Equal(new[]
            {
                PartyWireRig.Frame(ZoneS2COpcodes.NfyPartyTypeChange, Convert.FromHexString("0109090044e40000000002")),
                PartyWireRig.Frame(ZoneS2COpcodes.OnPartyNotifyUpdate, roster),
            }, await rig.FramesAsync(i));
    }

 /// A member whose character has left the world gets neither the 2692 nor the roster, and the roster the others
 /// receive lists it offline (rec+53 = 0). Every party send follows the same online-member rule (GetOnlineMember).
    [Fact]
    public async Task AcceptedChange_SkipsAMemberWhoseCharacterIsOutOfTheWorld()
    {
        await using var rig = await PartyWireRig.StartAsync(3);
        var leader = rig.AddPlayer(0, "Leader", mapId: 1100);
        var member = rig.AddPlayer(1, "Member", mapId: 1100);
        var away = rig.AddPlayer(2, "Away", mapId: 1100);
        var party = rig.TrackParty(0x74, [leader, member, away]);
        rig.Players.RemovePlayer(away.SessionId, out _);
        await rig.DrainAsync();

        await NewHandler(rig).HandleAsync(rig.Sessions[0], new byte[] { 0x02 });

        var roster = rig.Party.BuildRosterBody(party);
        Assert.Equal(3, roster[18]);
        Assert.Equal(new byte[] { 1, 1, 0 },
            new[] { roster[7 + 63 + 53], roster[7 + 63 + 208 + 53], roster[7 + 63 + 416 + 53] });
        Assert.Equal(new[]
        {
            PartyWireRig.Frame(ZoneS2COpcodes.NfyPartyTypeChange, Convert.FromHexString("0109090044e40000000002")),
            PartyWireRig.Frame(ZoneS2COpcodes.OnPartyNotifyUpdate, roster),
        }, await rig.FramesAsync(1));
        Assert.Empty(await rig.FramesAsync(2));
        Assert.Equal(2, party.PartyType);
    }

 /// The refusal: the leader alone receives 2691 code 15719, the type stays 0, and no 2692 or roster is sent.
 /// A request from a member who is not the leader is not answered.
    [Fact]
    public async Task LeaderOnADungeonMap_IsRefusedWith15719_AndNothingChanges()
    {
        await using var rig = await PartyWireRig.StartAsync(2);
        var leader = rig.AddPlayer(0, "DualStabUser", mapId: 410203);
        var member = rig.AddPlayer(1, "Member", mapId: 410203);
        var party = rig.TrackParty(0x72, [leader, member]);
        await rig.DrainAsync();

        await rig.Party.ChangeTypeAsync(leader.SessionId, 2, requesterOnDungeonMap: true);
        await rig.Party.ChangeTypeAsync(member.SessionId, 2, requesterOnDungeonMap: false);

        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.AnsPartyTypeChange, Convert.FromHexString("673d02")) },
            await rig.FramesAsync(0));
        Assert.Empty(await rig.FramesAsync(1));
        Assert.Equal(0, party.PartyType);
    }

 /// The dungeon-map rule over data/Map_List.json: the maps of the refused changes are dungeon maps, and none of
 /// the maps of the accepted changes is.
    [Theory]
    [InlineData(421203u, true)]
    [InlineData(410203u, true)]
    [InlineData(1100u, false)]
    [InlineData(4303u, false)]
    public void DungeonMap_FollowsMapListMapType(uint mapId, bool dungeon)
    {
        var row = MapList().SingleOrDefault(m => m.Map_Index == mapId);
        Assert.NotNull(row);
        Assert.Equal(dungeon, PartyTypeChangeHandler.IsDungeonMap(row));
    }

    [Fact]
    public void MapMissingFromMapList_IsNotADungeonMap()
        => Assert.False(PartyTypeChangeHandler.IsDungeonMap(null));

    private static PartyTypeChangeHandler NewHandler(PartyWireRig rig)
    {
        var services = new ServiceCollection();
        services.AddSingleton(rig.Party);
        services.AddSingleton(new GameDataStore()); 
        var provider = services.BuildServiceProvider();
        return new PartyTypeChangeHandler(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PartyTypeChangeHandler>.Instance, rig.Players, rig.Broadcast);
    }

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static List<MapData> MapList()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Map_List.json")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Map_List.json");
        var json = File.ReadAllBytes(Path.Combine(dir.FullName, "data", "Map_List.json"));
        return JsonSerializer.Deserialize<List<MapData>>(json, CaseInsensitive)!;
    }
}
