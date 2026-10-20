using RO2.Network.Packets;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The two 2398 OnChangeState pairs that a field duel sends about its duelists: the start pair right before the 2758
/// start message, and the end pair right after whatever closes the duel.
/// </summary>
/// <remarks>
/// <para>
/// The start pair stores the duel id and the duelists' sides (1002 and 1003) on every client on the map. The end pair
/// resets them to duel id 0 and side 2, the value every ordinary player state carries.
/// </para>
/// <para>
/// The start pair is sent to the whole map, one frame per duelist, the challenger's frame first. The end pair is sent to
/// the map after a duel closes, with the winner's or the challenger's frame first depending on how the duel ended.
/// </para>
/// </remarks>
public static class FieldPvPStatePackets
{
 /// <summary>Byte at blob+14 of the start pair. The end pair carries 0.</summary>
    internal const byte InDuelFlag = 1;

 /// <summary>Side at blob+19 of the end pair: 2, the side of a player outside any duel.</summary>
    internal const uint NoDuelSide = 2;

 /// <summary>
 /// The start-pair 2398 about <paramref name="duelist"/>: the duelist's handle twice, state 0, the duel id, the side
 /// (<see cref="FieldPvPStartPackets.ChallengerSide"/> for the challenger, <see cref="FieldPvPStartPackets.TargetSide"/>
 /// for the target) and a zero trailing value.
 /// </summary>
    public static byte[] BuildStart2398(uint duelist, uint duelId, uint side)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(CombatPackets.MsgTagChangeState);   
            p.WriteUInt32(duelist);                            
            p.WriteUInt32(duelist);                            
            p.WriteByte(CombatPackets.ActorState.PcNormal);    
            p.WriteByte(0);                                    
            p.WriteByte(InDuelFlag);                           
            p.WriteUInt32(duelId);                             
            p.WriteUInt32(side);                               
            p.WriteUInt32(0);                                  
        });
        return pw.ToArray();
    }

 /// <summary>The end-pair 2398 about <paramref name="duelist"/>: state 0, no duel, side 2.</summary>
    public static byte[] BuildEnd2398(uint duelist)
        => CombatPackets.BuildChangeState2398(duelist, CombatPackets.ActorState.PcNormal, trailing: NoDuelSide);

 /// <summary>
 /// Sends the start pair of <paramref name="duel"/> to its map, the challenger's frame first.
 /// </summary>
    public static async Task BroadcastStartPairAsync(BroadcastService broadcast, FieldPvPService.Duel duel)
    {
        var challenger = BuildStart2398(duel.Challenger, duel.Id, FieldPvPStartPackets.ChallengerSide);
        var target = BuildStart2398(duel.Target, duel.Id, FieldPvPStartPackets.TargetSide);
        await broadcast.BroadcastToMapAsync(duel.MapId, ZoneS2COpcodes.OnChangeState, w => w.WriteBytes(challenger));
        await broadcast.BroadcastToMapAsync(duel.MapId, ZoneS2COpcodes.OnChangeState, w => w.WriteBytes(target));
    }

 /// <summary>
 /// Sends the end pair about the two duelists to <paramref name="mapId"/>, with <paramref name="first"/>'s frame first.
 /// </summary>
    public static async Task BroadcastEndPairAsync(BroadcastService broadcast, uint mapId, uint first, uint second)
    {
        var a = BuildEnd2398(first);
        var b = BuildEnd2398(second);
        await broadcast.BroadcastToMapAsync(mapId, ZoneS2COpcodes.OnChangeState, w => w.WriteBytes(a));
        await broadcast.BroadcastToMapAsync(mapId, ZoneS2COpcodes.OnChangeState, w => w.WriteBytes(b));
    }
}
