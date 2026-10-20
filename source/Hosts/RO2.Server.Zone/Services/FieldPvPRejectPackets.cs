using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>RMI 2742 AnsReqErrorFiledPvP: a field duel challenge refused, sent to the challenger only.</summary>
/// <remarks>
/// <para>
/// Layout: <c>[u16 selfLen 10][u16 msgTag][u32 self = the challenger][u16 code]</c>. The code names the reason for the refusal.
/// </para>
/// <para>
/// The client prints a message for each known code and then closes the duel windows and resets its duel state, so a refused
/// challenger is never left waiting. Codes that the client does not list show no message.
/// </para>
/// <para>
/// This server sends the codes it can decide from its own state: the target already in a duel or with a pending challenge,
/// either player being dead, and the two players being farther apart than <see cref="FieldPvPService.DuelRange"/>.
/// Other codes are not sent.
/// </para>
/// </remarks>
public static class FieldPvPRejectPackets
{
 /// <summary>2742 msgTag (u16 at blob+2).</summary>
    internal const ushort MsgTagReject = 58461;

 /// <summary>Code: the target is already in an accepted duel.</summary>
    internal const ushort TargetInBattle = 12;
 /// <summary>Code: the target already has a pending challenge.</summary>
    internal const ushort TargetOnBattleCue = 13;
 /// <summary>Code: the challenger is dead. Client-defined; never sent on the wire.</summary>
    internal const ushort ChallengerDead = 19;
 /// <summary>Code: the target is dead. Client-defined; never sent on the wire.</summary>
    internal const ushort TargetDead = 20;
 /// <summary>
 /// Code: the two players stand farther apart than <see cref="FieldPvPService.DuelRange"/>.
 /// </summary>
    internal const ushort TooFar = 21;

 /// <summary>2742 body: <c>[u16 10][u16 msgTag][u32 self][u16 code]</c>.</summary>
    public static byte[] BuildReject2742(uint self, ushort code)
    {
        using var pw = new PacketWriter(16);
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagReject);
            w.WriteUInt32(self);
            w.WriteUInt16(code);
        });
        return pw.ToArray();
    }
}
