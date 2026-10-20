using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the "ActorState" notify family — the broadcast/self notifications that ride the
/// compact-length ByteArray messages. This is a sibling of
/// <see cref="ActorStatePackets"/>; it lives in its own file only so parallel edits to
/// <c>ActorStatePackets</c> do not clash. Every opcode here is COMPACT-BA enveloped on the
/// wire, exactly like <see cref="ActorStatePackets"/>:
/// <code>
///   body = [compactLen(selfLen)][u16 selfLen][u16 msgTag][fields...]
///          selfLen = 2 + payload.Length,  payload = [u16 msgTag][fields]
/// </code>
/// <see cref="PacketWriter.WriteCompactBABody"/> guarantees the first body byte is a valid compact
/// marker in {1,2,4,8}; a raw body would make the client's compact-length reader fail, hand the
/// per-opcode stub an empty ByteArray and NULL-deref (an access violation on the remote client).
/// </summary>
/// <remarks>
/// Each opcode has exactly ONE body length and a constant <c>msgTag</c> (the u16 at blob+2); the envelope
/// family is COMPACT_INNER.
/// </remarks>
public static class ActorStateNotifyPackets
{
    // msgTags — the u16 at blob+2 (body+4), constant for each opcode.
    private const ushort TagOnSitDownNotify = 58113;   // 0xE301, 2323

    // Pose enum carried at blob+12 of 2323 OnSitDownNotify.
    /// <summary>Pose value for a standing actor.</summary>
    internal const byte PoseStand = 0;

    /// <summary>Pose value for a sitting actor.</summary>
    internal const byte PoseSit = 9;

    /// <summary>
    /// 2323 OnSitDownNotify — the BROADCAST half of a sit/stand (its sibling 2322 OnSitDown is the
    /// self-ack). Body 27, blob 25:
    /// <c>[u16 innerSize=25][u16 msgTag=58113][u32 actorHandle][u32 actorHandle][u8 state][f32 x][f32 y][f32 z]</c>.
    /// The two handle slots (blob+4 and blob+8) are byte-identical (the sitter's own handle, PC tag 0x11); blob+8
    /// is the one the client uses for the CActorRegistry lookup. blob+12 is the pose enum: <see cref="PoseSit"/> (<c>9</c>) = sit and
    /// <see cref="PoseStand"/> (<c>0</c>) = stand; map the C2S flag (0x01 sit / 0x00 stand) with <see cref="StateEnum"/>. The
    /// trailing 12 bytes are the actor's world position (X,Y,Z): the client does not read them, but they are
    /// part of the length-exact body.
    /// </summary>
    public static byte[] BuildOnSitDownNotify(uint actorHandle, byte state, float x, float y, float z)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnSitDownNotify);
            p.WriteUInt32(actorHandle);
            p.WriteUInt32(actorHandle);
            p.WriteByte(state);
            p.WriteSingle(x);
            p.WriteSingle(y);
            p.WriteSingle(z);
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Maps the C2S ReqActorSitDown (1308) flag (0x01 sit / 0x00 stand) to the S2C 2323 pose enum
    /// on the wire: sit -&gt; <see cref="PoseSit"/>, stand -&gt; <see cref="PoseStand"/>. Any non-zero C2S flag is treated
    /// as "sit".
    /// </summary>
    public static byte StateEnum(byte c2sFlag) => c2sFlag != 0 ? PoseSit : PoseStand;
}
