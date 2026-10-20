using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The party role round of the dungeon finder: 2822 asks a party member for a role, the member answers with 1568 (or
/// 1574 to cancel), 2834 answers the 1568, and 2831 closes the round for every member. All are NONE family messages with a
/// fixed size and no msgTag. <see cref="DungeonFinderPartyRound"/> decides which message is sent to whom.
/// </summary>
/// <remarks>
/// <para>
/// The 2822 and 1574 layouts follow the client's reader, since no captured frame exists for them.
/// </para>
/// </remarks>
public static class FindPartyRolePackets
{
 /// <summary>2834 result 0: the role was accepted.</summary>
    public const ushort SelectRoleAccepted = 0;

 /// <summary>2834 result 8: the role's slot is already filled.</summary>
    public const ushort SelectRoleSlotTaken = 8;

 /// <summary>
 /// 2831 result 2: the round ended without a party. Any non-zero result shows the round-failed notice and resets the
 /// finder window.
 /// </summary>
    public const ushort RoundFailed = 2;

 /// <summary>
 /// 2822 NfyFindParty_Party_SelectRolePlease: empty body. The client shows the role selection window, or answers with a
 /// cancel on its own when the receiver is inside a dungeon.
 /// </summary>
    public static ReadOnlyMemory<byte> SelectRolePlease2822 => ReadOnlyMemory<byte>.Empty;

 /// <summary>Parses 1568 ReqFindParty_Party_SelectRole: NONE family with one byte of role flags. False on any other length.</summary>
    public static bool TryParseSelectRole1568(ReadOnlySpan<byte> body, out byte roleFlags)
    {
        roleFlags = 0;
        if (body.Length != 1) return false;
        roleFlags = body[0];
        return true;
    }

 /// <summary>
 /// 2834 AnsFindParty_Party_SelectRole: NONE family with one u16 result, <c>0000</c> (<see cref="SelectRoleAccepted"/>)
 /// or <c>0800</c> (<see cref="SelectRoleSlotTaken"/>).
 /// </summary>
    public static byte[] BuildSelectRoleAnswer2834(ushort result)
    {
        var body = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

 /// <summary>
 /// 2831 NfyResult_FindParty_Party_SelectRole with <see cref="RoundFailed"/>: NONE family, 5 bytes
 /// <c>[u16 result][u8][u8][u8]</c>. The three trailing bytes are read only for result 0, which is never sent here.
 /// </summary>
    public static byte[] BuildRoundFailed2831()
    {
        var body = new byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(body, RoundFailed);
        return body;
    }
}
