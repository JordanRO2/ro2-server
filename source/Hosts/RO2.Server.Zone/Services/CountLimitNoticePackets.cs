using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builder for RMI 2894 <c>NfyCountLimitItemNotice</c>, the server-wide notice sent when a player does
/// something worth announcing, such as a high refine. The notice names the acting player and is delivered
/// to all connected players.
/// </summary>
/// <remarks>
/// <para>
/// Body is <c>[u32 blobLen][blob]</c>. The blob holds the server name and character name as UTF-16 strings,
/// the item id, a constant trailer, and the kind with its two parameters:
/// <c>[u16 0][u16 msgTag][u32 0][WStr16 server][WStr16 name][u32 itemId][u32 itemId2][u32 trailer][u16 kind][u16 a][u16 b]</c>.
/// </para>
/// <para>
/// The notice is delivered to every connected client regardless of map.
/// </para>
/// </remarks>
public static class CountLimitNoticePackets
{
    /// <summary>Message tag of 2894 (58486, 0xE476), at blob+2.</summary>
    internal const ushort MsgTag = 58486;

    /// <summary>
    /// Constant u32 that follows <c>itemId2</c> in every notice. Its meaning is not established, so it is sent as is.
    /// </summary>
    internal const uint TrailerConstant = 0x000EC9FF;

    /// <summary>
    /// Kind value of a refine announcement (<c>a</c> = grade before, <c>b</c> = grade after). The only kind this
    /// server sends; the protocol also defines kind 3 (random-box item) and kind 2 (random-set prize).
    /// </summary>
    internal const ushort KindRefine = 8;

    /// <summary>Value of the reserved u16 at blob+0 and u32 at blob+4.</summary>
    private const uint Reserved = 0;

    /// <summary>
    /// Builds the 2894 body (<c>[u32 blobLen][blob]</c>), where <c>blobLen = 30 + 2 * len(server) + 2 * len(name)</c>.
    /// </summary>
    /// <param name="itemId2">Equal to <paramref name="itemId"/> except for kind 2, where it is the box opened.</param>
    public static byte[] BuildCountLimitItemNotice2894(string serverName, string characterName,
        uint itemId, uint itemId2, ushort kind, ushort a, ushort b)
    {
        using var pw = new PacketWriter(64 + 2 * (serverName.Length + characterName.Length));
        var lenPos = pw.Reserve(4);
        var start = pw.Length;
        pw.WriteUInt16((ushort)Reserved);   // blob+0 (not a selfLen)
        pw.WriteUInt16(MsgTag);             // blob+2
        pw.WriteUInt32(Reserved);           // blob+4
        pw.WriteUnicodeString(serverName);  // blob+8, [u16 nChars][UTF-16LE]
        pw.WriteUnicodeString(characterName);
        pw.WriteUInt32(itemId);
        pw.WriteUInt32(itemId2);
        pw.WriteUInt32(TrailerConstant);
        pw.WriteUInt16(kind);
        pw.WriteUInt16(a);
        pw.WriteUInt16(b);
        pw.WriteAt(lenPos, (uint)(pw.Length - start));
        return pw.ToArray();
    }

    /// <summary>
    /// Builds a kind-8 refine announcement. <c>a</c> is the grade before the refine and <c>b</c> the grade after.
    /// Only refine grades that are announced by the refine rate table are sent.
    /// </summary>
    public static byte[] BuildRefineAnnounce2894(string serverName, string characterName, uint itemId,
        int oldGrade, int newGrade) =>
        BuildCountLimitItemNotice2894(serverName, characterName, itemId, itemId, KindRefine,
            (ushort)oldGrade, (ushort)newGrade);
}
