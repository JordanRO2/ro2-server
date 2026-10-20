using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// One row of the 2622 AnsPartyApplySearch listing: a fixed 101-byte record.
/// </summary>
/// <code>
/// +0 u8 category Find_Party_Category key: the category name, and the map the destination is looked up in
/// +1 u8 role flags bit 1 = role 1, bit 4 = role 2, bit 2 = role 3
/// +2 u8 difficulty index into {All, Easy, Normal, Hard}
/// +3 u8 class the poster's combat class
/// +4 u8 reserved 0
/// +5 u32 destination looked up in the category's destination map; an unknown id shows "All"
/// +9 wchar_t[21] poster name
/// +51 wchar_t[21] title, NUL-terminated
/// +93 u64 poster GID
/// </code>
/// <remarks>
/// Every field except class, name and GID comes from the poster's 1458 request (see <see cref="PartyApplyBoard.TryParseApplyMsg"/>).
/// </remarks>
public readonly record struct PartyApplyListing(
    byte Category, byte RoleFlags, byte Difficulty, byte Job, uint Destination,
    string PosterName, string Title, ulong PosterGid);

/// <summary>2622 AnsPartyApplySearch, the answer to 1460 ReqPartyMatchingSearchApply.</summary>
public static class PartyApplySearchPackets
{
 /// <summary>Width of one 2622 row in bytes.</summary>
    public const int RowBytes = 101;

 /// <summary>
 /// Builds the 2622 body: <c>[compactLen][u16 page][u16 pages][u8 n][n x 101-byte row]</c>. Page and pages are
 /// written back for the page caption. With no rows the body is <see cref="PartyStatePackets.BuildAnsPartyApplySearchEmpty"/>.
 /// Names and titles are NUL-terminated and zero-padded.
 /// </summary>
    public static byte[] BuildAnsPartyApplySearch(ushort page, ushort pages, IReadOnlyList<PartyApplyListing> rows)
    {
        
        var count = Math.Min(rows.Count, PartyApplyBoard.PageSize);
        using var blob = new PacketWriter(5 + RowBytes * count);
        blob.WriteUInt16(page);
        blob.WriteUInt16(pages);
        blob.WriteByte((byte)count);
        for (var i = 0; i < count; i++)
        {
            var r = rows[i];
            blob.WriteByte(r.Category);                                                  
            blob.WriteByte(r.RoleFlags);                                                 
            blob.WriteByte(r.Difficulty);                                                
            blob.WriteByte(r.Job);                                                       
            blob.WriteByte(0);                                                           
            blob.WriteUInt32(r.Destination);                                             
            blob.WriteBytes(FixedWideTerminated(r.PosterName, PartyApplyBoard.TextChars)); 
            blob.WriteBytes(FixedWideTerminated(r.Title, PartyApplyBoard.TextChars));      
            blob.WriteUInt64(r.PosterGid);                                               
        }

        using var pw = new PacketWriter(blob.Length + 9);
        pw.WriteCompactLength(blob.Length);
        pw.WriteBytes(blob.GetWrittenSpan());
        return pw.ToArray();
    }

 /// <summary>
 /// UTF-16 text in a <paramref name="bufferChars"/>-wide zero-padded buffer. The last code unit is kept free for the
 /// NUL terminator, which the client reads without a bound.
 /// </summary>
    private static byte[] FixedWideTerminated(string? value, int bufferChars)
    {
        var buf = new byte[bufferChars * 2];
        if (!string.IsNullOrEmpty(value))
        {
            var take = Math.Min(value.Length, bufferChars - 1);
            System.Text.Encoding.Unicode.GetBytes(value.AsSpan(0, take), buf.AsSpan(0, take * 2));
        }
        return buf;
    }
}
