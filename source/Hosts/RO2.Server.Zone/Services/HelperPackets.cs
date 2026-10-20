using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Party Helper flag: handles 1614 ReqHelperDeclare, builds the 2902 AnsHelperDeclare answer and the 2903
/// NfyHelperNotice notice, and holds the client's rule for who may be a Helper.
/// </summary>
/// <remarks>
/// <para>
/// The flag is stored in the 208-byte party member record. The three messages carry their fields directly,
/// with no length prefix or inner header.
/// </para>
/// <para>
/// A member may be a Helper when at least one other party member is 10 or more levels lower.
/// </para>
/// </remarks>
public static class HelperPackets
{
 /// <summary>Offset of the Helper flag in the 208-byte party member record.</summary>
    public const int RecHelperFlagOff = 63;

 /// <summary>Helper flag values: 1 declared, 0 quit.</summary>
    public const byte FlagDeclared = 1;
    public const byte FlagQuit = 0;

 /// <summary>1614 body length: u64 GID plus u8 flag.</summary>
    public const int DeclareBodyLen = 9;

 /// <summary>Level difference a Helper must have over another party member.</summary>
    public const int LevelGap = 10;

 /// <summary>
 /// Returns whether a member at <paramref name="level"/> may be a Helper: at least one of the other members'
 /// levels (<paramref name="otherLevels"/>) is <see cref="LevelGap"/> or more below it.
 /// </summary>
    public static bool CanBeHelper(byte level, IEnumerable<byte> otherLevels)
    {
        foreach (var other in otherLevels)
            if (level >= other + LevelGap)
                return true;
        return false;
    }

 /// <summary>Builds the 2902 AnsHelperDeclare body: <c>[u64 gid][u8 flag][u16 result]</c>.</summary>
    public static byte[] BuildAnsHelperDeclare2902(ulong gid, byte flag, ushort result)
    {
        var body = new byte[11];
        BinaryPrimitives.WriteUInt64LittleEndian(body, gid);
        body[8] = flag;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9), result);
        return body;
    }

 /// <summary>Builds the 2903 NfyHelperNotice body: <c>[u64 member gid][u8 flag]</c>.</summary>
    public static byte[] BuildHelperNotice2903(ulong gid, byte flag)
    {
        var body = new byte[9];
        BinaryPrimitives.WriteUInt64LittleEndian(body, gid);
        body[8] = flag;
        return body;
    }
}
