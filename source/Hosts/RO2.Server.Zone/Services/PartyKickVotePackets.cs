using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Server-to-client bodies of the party kick vote: the result of a kick request (2826) and the vote notice (2829).
/// Both use the plain body layout, without a length prefix or msgTag.
/// </summary>
public static class PartyKickVotePackets
{
 /// <summary>Result code: the vote started.</summary>
    public const ushort ResultAccepted = 0;

 /// <summary>Result code: the target cannot be kicked yet. The second field holds the remaining cooldown in seconds.</summary>
    public const ushort ResultCooldown = 15736;

 /// <summary>Result code: a kick vote is already in progress.</summary>
    public const ushort ResultUnderway = 15737;

 /// <summary>
 /// Builds 2826 AnsPartyMemberKickVoteStart, a 4-byte body <c>[u16 result][u16 secondsLeft]</c>.
 /// </summary>
 /// <param name="result">Result code, such as <see cref="ResultAccepted"/> or <see cref="ResultCooldown"/>.</param>
 /// <param name="secondsLeft">Remaining seconds, used with <see cref="ResultCooldown"/>.</param>
    public static byte[] BuildAnsKickVoteStart2826(ushort result, ushort secondsLeft)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), secondsLeft);
        return body;
    }

 /// <summary>
 /// Builds 2829 NfyPartyMemberKickVote, a 16-byte body <c>[u64 initiatorGid][u64 targetGid]</c>. The client
 /// shows the vote with the initiator's name first and the target's second.
 /// </summary>
 /// <param name="initiatorGid">GID of the player who started the vote.</param>
 /// <param name="targetGid">GID of the player the vote targets.</param>
    public static byte[] BuildNfyKickVote2829(ulong initiatorGid, ulong targetGid)
    {
        var body = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(body, initiatorGid);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), targetGid);
        return body;
    }
}
