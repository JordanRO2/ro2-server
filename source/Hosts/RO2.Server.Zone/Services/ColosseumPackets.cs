using System;
using System.Collections.Generic;
using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the colosseum (PvP arena) notification packets. Each body is sized exactly as the client expects, so no fixed-size read can run past the data.
/// </summary>
/// <remarks>
/// Some bodies are raw fixed-size payloads, some are length-prefixed lists, and the scoreboard is a 32-bit length-prefixed blob. Length-prefixed bodies must not be given extra markers.
/// </remarks>
public static class ColosseumPackets
{
 /// <summary>Maximum player name length in characters.</summary>
    public const int NameBufferChars = 14;
    private const int NameBufferBytes = NameBufferChars * 2; 
    private const int ScoreRegionBytes = 15;                 
    private const int ScoreRecordBytes = NameBufferBytes + ScoreRegionBytes; 

 /// <summary>
 /// Builds the join notification (2971): the ready state of each registrant, as a length-prefixed list of bytes.
 /// </summary>
 /// <param name="memberStates">One state byte per registrant.</param>
    public static byte[] BuildJoinMsg(IReadOnlyList<byte> memberStates)
    {
        using var w = new PacketWriter(4 + memberStates.Count);
        w.WriteCompactLength(memberStates.Count);
        for (int i = 0; i < memberStates.Count; i++)
            w.WriteByte(memberStates[i]);
        return w.ToArray();
    }

 /// <summary>
 /// Builds the party state notification (2973): a room id followed by a length-prefixed list of member state bytes.
 /// </summary>
 /// <param name="roomId">Identifier of the queue room.</param>
 /// <param name="memberStates">One state byte per member.</param>
    public static byte[] BuildPartyState(uint roomId, IReadOnlyList<byte> memberStates)
    {
        using var w = new PacketWriter(8 + memberStates.Count);
        w.WriteUInt32(roomId);
        w.WriteCompactLength(memberStates.Count);
        for (int i = 0; i < memberStates.Count; i++)
            w.WriteByte(memberStates[i]);
        return w.ToArray();
    }

 /// <summary>Builds the cancel acknowledgement (2975), which has an empty body.</summary>
    public static byte[] BuildInCancelAck() => Array.Empty<byte>();

 /// <summary>Builds the ranking update notification (2887): a single byte with the ranked player count.</summary>
 /// <param name="value">The ranked player count.</param>
    public static byte[] BuildRankingUpdate(byte value) => new[] { value };

 /// <summary>One scoreboard row: a player name and its per-match counter.</summary>
    public readonly record struct ScoreEntry(string Name, byte Score);

 /// <summary>
 /// Builds the scoreboard notification (2889): the number of rows, then one fixed-size record per row with the name and the match counter.
 /// </summary>
 /// <remarks>
 /// The server does not run colosseum matches, so every score is 0. An empty list sends the empty scoreboard.
 /// </remarks>
    public static byte[] BuildScoreBoard(IReadOnlyList<ScoreEntry> entries)
    {
        int count = entries.Count;
        int innerLen = 2 + ScoreRecordBytes * count; 
        using var w = new PacketWriter(4 + innerLen);
        w.WriteUInt32((uint)innerLen);
        w.WriteUInt16((ushort)count);
        Span<byte> rec = stackalloc byte[ScoreRecordBytes];
        for (int i = 0; i < count; i++)
        {
            rec.Clear();
            WriteName(rec.Slice(0, NameBufferBytes), entries[i].Name);
            rec[ScoreRecordBytes - 1] = entries[i].Score; 
            w.WriteBytes(rec);
        }
        return w.ToArray();
    }

 /// <summary>Writes a name as UTF-16LE into a fixed, zero-padded buffer, truncated to the maximum name length.</summary>
    private static void WriteName(Span<byte> dst, string? name)
    {
        dst.Clear();
        if (string.IsNullOrEmpty(name)) return;
        int chars = Math.Min(name.Length, NameBufferChars);
        Encoding.Unicode.GetBytes(name.AsSpan(0, chars), dst);
    }
}
