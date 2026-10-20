using System.Buffers.Binary;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Builds and sends the dungeon level selection notice (2760 NfyDungeonLevelSelectInfo) to party members when
/// the party leader selects a dungeon level with 1521 AnsDungeonLevelSelect.
/// </summary>
/// <remarks>
/// The notice has a fixed 6-byte body: <c>[u32 DungeonGroupID][u16 Map_Level]</c>. It is sent only when the
/// leader's selection opens a new dungeon instance, and only for a leader of a party. It goes to every other
/// in-world party member, never to the sender, and each member receives one copy per in-world party member.
/// The offered level is recorded for each notified member, so a later teleport request (1531) enters the
/// offered level.
/// </remarks>
public static class DungeonLevelSelectInfoNotify
{
    private sealed record DungeonOffer(uint DungeonGroupId, ushort MapLevel);

    
    
    
    private static readonly ConditionalWeakTable<PlayerState, DungeonOffer> Offers = new();

 /// <summary>
 /// Returns the Map_Level of the last 2760 offered to <paramref name="member"/> for
 /// <paramref name="dungeonGroupId"/>. Returns false when no such offer exists.
 /// </summary>
    public static bool TryGetOfferedLevel(PlayerState member, uint dungeonGroupId, out ushort mapLevel)
    {
        if (Offers.TryGetValue(member, out var offer) && offer.DungeonGroupId == dungeonGroupId)
        {
            mapLevel = offer.MapLevel;
            return true;
        }
        mapLevel = 0;
        return false;
    }

 /// <summary>Builds the 6-byte 2760 body: <c>[u32 DungeonGroupID][u16 Map_Level]</c>.</summary>
    public static byte[] Build2760(uint dungeonGroupId, ushort mapLevel)
    {
        var body = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(body, dungeonGroupId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), mapLevel);
        return body;
    }

 /// <summary>
 /// Returns the number of members of <paramref name="party"/> currently in the world, including the leader.
 /// Offline members are not counted.
 /// </summary>
    public static int InWorldMemberCount(PartyState party, PlayerStateManager players)
    {
        var count = 0;
        
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            if (players.GetPlayer(memberId) is not null)
                count++;
        }
        return count;
    }

 /// <summary>
 /// Sends the notice to the other in-world members of <paramref name="sender"/>'s party when the sender leads
 /// it, and records the offer for each notified member. Does nothing for a solo player or a non-leader.
 /// Returns the number of members notified.
 /// </summary>
    public static async Task<int> SendToPartyAsync(PlayerState sender, uint dungeonGroupId, ushort mapLevel,
        IServiceProvider services, ILogger logger)
    {
        var party = services.GetRequiredService<PartyService>().GetPlayerParty(sender.SessionId);
        if (party is null || party.LeaderSessionId != sender.SessionId)
            return 0;

        var players = services.GetRequiredService<PlayerStateManager>();
        var broadcast = services.GetRequiredService<BroadcastService>();
        var body = Build2760(dungeonGroupId, mapLevel);
        var copies = InWorldMemberCount(party, players);
        var sent = 0;
        
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            if (memberId == sender.SessionId) continue;
            
            
            
            var member = players.GetPlayer(memberId);
            if (member is null) continue;
            var memberSession = broadcast.GetSession(memberId);
            if (memberSession is null) continue;
            
            Offers.AddOrUpdate(member, new DungeonOffer(dungeonGroupId, mapLevel));
            
            for (var i = 0; i < copies; i++)
                await memberSession.SendInternalRmiAsync(ZoneS2COpcodes.NfyDungeonLevelSelectInfo, body);
            sent++;
        }

        logger.LogInformation("{Name} (party {PartyId} leader) opened dungeon group {Group} level {Level}: 2760 x{Copies} sent to {Sent} member(s)",
            sender.Name, party.PartyId, dungeonGroupId, mapLevel, copies, sent);
        return sent;
    }
}
