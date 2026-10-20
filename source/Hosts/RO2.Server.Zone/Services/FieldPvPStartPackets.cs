using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// 2758 NfyStartFiledPvP: the start of an accepted field duel, sent to both duelists a few seconds after the target's
/// acceptance (2737 result 12, <see cref="FieldPvPPackets.AcceptAccepted"/>).
/// </summary>
/// <remarks>
/// <para>
/// Body (compact envelope, 18 bytes): <c>[u16 selfLen 16][u16 msgTag][u32 recipient's own handle][u32 challenger side][u32 target side]</c>.
/// Each copy starts with its own recipient's handle. The sides are fixed protocol values: 1002 for the challenger and 1003 for the target.
/// </para>
/// <para>
/// The client's consumer sets its duel state and stores the duel id and side into the two duelists' actors, then opens the duel windows.
/// A start message must never follow a duel that is already closed.
/// </para>
/// <para>
/// The message is sent about 3 seconds after the acceptance, and both duelists receive it. A
/// <see cref="FieldPvPStatePackets"/> start pair is sent just before it so that other clients on the map see the duel.
/// </para>
/// </remarks>
public static class FieldPvPStartPackets
{
 /// <summary>2758 msgTag.</summary>
    internal const ushort MsgTagStart = 58462;

 /// <summary>Challenger's side value at blob+8 (applied to the challenger actor).</summary>
    internal const uint ChallengerSide = 1002;

 /// <summary>Target's side value at blob+12 (applied to the target actor).</summary>
    internal const uint TargetSide = 1003;

 /// <summary>
 /// Delay after the 2737 result 12 before the 2758 is sent. This server has no duel tick, so it sends at the 3 second floor.
 /// </summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(3);

 /// <summary>
 /// 2758 for one duelist: <c>[u32 self][u32 1002][u32 1003]</c> in the compact envelope.
 /// </summary>
    public static byte[] BuildStart2758(uint self)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagStart);
            w.WriteUInt32(self);
            w.WriteUInt32(ChallengerSide);
            w.WriteUInt32(TargetSide);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Whether the duel accepted as <paramref name="accepted"/> still starts: the service still holds the same object
 /// (<paramref name="current"/>, looked up by its id) and it is still accepted. A duel closed in between, by a cancel,
 /// a forfeit, a refusal, a timeout or a stale-duel cleanup, is no longer in the service.
 /// </summary>
    internal static bool StillStarts(FieldPvPService.Duel accepted, FieldPvPService.Duel? current)
        => current is not null && ReferenceEquals(current, accepted)
           && current.State == FieldPvPService.DuelState.Accepted;
}
