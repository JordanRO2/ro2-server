using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>One ranking record: an element of the 2964 list, or the body of 2965.</summary>
/// <param name="Rank">Position on the board, or 0 when unranked.</param>
/// <param name="PreviousRank">Position on the board this record replaced, or 0 when it was unranked.</param>
/// <param name="ClassId">The combat class (a JobInfo id).</param>
/// <param name="Profession">The creation profession (a ProJob_Type id).</param>
/// <param name="Score">The value the board ranks by.</param>
/// <param name="Name">Character name.</param>
/// <param name="Guild">Guild name, empty when none.</param>
public readonly record struct RankRow(uint Rank, uint PreviousRank, byte ClassId, byte Profession, ulong Score,
    string Name, string Guild);

/// <summary>
/// Builders for the ranking bodies: 2964 AnsRankingList (the board) and 2965 AnsRanking (the requester's own record).
/// </summary>
/// <remarks>
/// A record is a u32 rank, a u32 previous rank, a u8 class, a u8 profession, a u64 score, then the name and the guild
/// as UTF-16 strings with a u16 length prefix. A 2964 body is a compact length, the length-prefixed records, and a u16
/// result: 0 with rows, or 1 when the board is empty. A 2965 body is one length-prefixed record and a u16 result of 0.
/// </remarks>
public static class RankingPackets
{
 /// <summary>The most rows one board carries.</summary>
    public const int BoardSize = 10;

 /// <summary>2964 result with rows.</summary>
    public const ushort ResultOk = 0;

 /// <summary>2964 result of an empty board.</summary>
    public const ushort ResultNoRows = 1;

 /// <summary>
 /// 2964 AnsRankingList: the first <see cref="BoardSize"/> rows with result 0, or an empty board with result 1 when
 /// there are none.
 /// </summary>
    public static byte[] Build2964(IReadOnlyList<RankRow> rows)
    {
        int n = Math.Min(rows.Count, BoardSize);
        using var w = new PacketWriter();
        w.WriteCompactLength(n);
        for (int i = 0; i < n; i++)
            WriteLengthPrefixedRecord(w, rows[i]);
        w.WriteUInt16(n == 0 ? ResultNoRows : ResultOk);
        return w.ToArray();
    }

 /// <summary>2965 AnsRanking: the requester's own record and result 0.</summary>
    public static byte[] Build2965(RankRow self)
    {
        using var w = new PacketWriter();
        WriteLengthPrefixedRecord(w, self);
        w.WriteUInt16(ResultOk);
        return w.ToArray();
    }

 /// <summary><c>u32 recordLen</c> then the record.</summary>
    private static void WriteLengthPrefixedRecord(PacketWriter w, RankRow row)
    {
        int lenPos = w.Reserve(4);
        int start = w.Length;
        WriteRecord(w, row);
        w.WriteAt(lenPos, (uint)(w.Length - start));
    }

 /// <summary>The record itself (see the type remarks).</summary>
    internal static void WriteRecord(PacketWriter w, RankRow row)
    {
        w.WriteUInt32(row.Rank);
        w.WriteUInt32(row.PreviousRank);
        w.WriteByte(row.ClassId);
        w.WriteByte(row.Profession);
        w.WriteUInt64(row.Score);
        w.WriteUnicodeString(row.Name);
        w.WriteUnicodeString(row.Guild);
    }
}
