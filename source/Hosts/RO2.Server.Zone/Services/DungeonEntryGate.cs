using System.Buffers.Binary;
using RO2.Dungeons.Domain;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Entry checks for instance dungeons, applied to a 1521 AnsDungeonLevelSelect and, for the entry count, to a
/// 1531 ReqPartyDungeonTeleport. A refusal is a lone 2556 AnsChannelMove carrying the result code.
/// </summary>
/// <remarks>
/// <para>
/// The client does not check entry conditions itself; every refusal comes from the server. A nonzero result at
/// the start of the 2556 is shown as a system message.
/// </para>
/// <para>
/// Checks run in this order: level range, quest completion, then the entry count. Party-only entry is enforced
/// for the Training Ground map, which opens a room for the party.
/// </para>
/// </remarks>
public static class DungeonEntryGate
{
 /// <summary>
 /// True when an entry into the Training Ground opens its room: no player other than the requester is on the
 /// room's map.
 /// </summary>
    public static bool OpensTrainingRoom(IEnumerable<PlayerState> playersOnMap, uint requesterSessionId)
    {
        foreach (var other in playersOnMap)
        {
            if (other.SessionId != requesterSessionId)
                return false;
        }
        return true;
    }

 /// <summary>Builds the 81-byte refusal: <c>[u32 accountId][u16 code]</c> followed by zeros.</summary>
    public static byte[] BuildRefusal2556(uint accountId, ushort result)
    {
        var body = new byte[DungeonChannelHandOff.AnsChannelMoveBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(body, accountId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), result);
        return body;
    }

 /// <summary>Sends the refusal 2556 for <paramref name="result"/>.</summary>
    public static Task RefuseAsync(ProudSession session, uint accountId, ushort result)
        => session.SendInternalRmiAsync(ZoneS2COpcodes.AnsChannelMove, BuildRefusal2556(accountId, result));
}
