using System;
using System.Collections.Generic;
using RO2.Domain.Entities;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// S2C body builders for the card book and card composition packets.
/// </summary>
/// <remarks>
/// <para>
/// These packets have no compact envelope and no message tag. The body is written directly.
/// </para>
/// <para>
/// The book record is [u32 cardId][u8 state][u8 copies][u8 enforceGrade][u8 kind]. See <see cref="BuildCardBookList2587"/> for the full list and <see cref="BuildRegistCardBook2588"/> for the single-record form.
/// </para>
/// </remarks>
public static class CardPackets
{
    
 /// <summary>Composition result code: the composition was attempted and failed. Both card ids are 0.</summary>
    internal const ushort ComposeFailed = 22871;

 /// <summary>Composition result code: the request was refused before anything happened.</summary>
    internal const ushort ComposeRejected = 24109;

 /// <summary>Put result code: the card was not put into the book. The card id is 0.</summary>
    internal const ushort CardBookPutFailed = 22873;

 /// <summary>
 /// Builds 2587 AnsCardBookList, the contents of the card book.
 /// </summary>
 /// <remarks>
 /// <para>
 /// Body: [u32 length][u32 actorHandle][u16 recordCount][8-byte records]. An empty book is a 10-byte body. The record count is the number of records, not the number of cards.
 /// </para>
 /// <para>
 /// Every registered card is listed, with or without copies. Cards are sorted by card grade, then by card id. A card's first two records (kind 0 and kind 1) are always sent, even when empty. Records for copies follow in ascending order of enforce grade and kind.
 /// </para>
 /// <para>
 /// The state byte is 2 for a registered card that has not been seen yet, and 1 for a seen card. It is not the card grade.
 /// </para>
 /// <para>
 /// A copy left out of this list cannot be taken off the card after a relog, so every copy the book holds is listed.
 /// </para>
 /// </remarks>
 /// <param name="actorHandle">The local player's actor handle.</param>
 /// <param name="registered">The character's registered cards with their state, in any order.</param>
 /// <param name="copies">The book's copies, one entry per copy.</param>
 /// <param name="gradeTypeOf">Gets a card's grade, the first sort key.</param>
    public static byte[] BuildCardBookList2587(uint actorHandle, IReadOnlyList<CardRegistryEntry> registered,
        IReadOnlyList<CardBookEntry> copies, Func<uint, byte> gradeTypeOf)
    {
        var state = new Dictionary<uint, byte>();
        foreach (var r in registered)
            state.TryAdd(r.CardId, r.State == CardRegistryStore.StateNew ? CardRegistryStore.StateNew : CardRegistryStore.StateSeen);

        
        
        var byCard = new Dictionary<uint, SortedDictionary<(byte Grade, byte Kind), int>>();
        foreach (var e in copies)
        {
            if (!byCard.TryGetValue(e.CardId, out var byKey))
            {
                byKey = new SortedDictionary<(byte Grade, byte Kind), int>();
                byCard[e.CardId] = byKey;
            }
            var key = (e.EnforceGrade, e.Kind == 0 ? (byte)0 : (byte)1);
            byKey[key] = byKey.GetValueOrDefault(key) + 1;
            state.TryAdd(e.CardId, CardRegistryStore.StateSeen);
        }

        var order = new List<uint>(state.Keys);
        order.Sort((a, b) =>
        {
            int byGradeType = gradeTypeOf(a).CompareTo(gradeTypeOf(b));
            return byGradeType != 0 ? byGradeType : a.CompareTo(b);
        });

        int recordCount = 0;
        foreach (var cardId in order)
        {
            recordCount += 2;   
            if (byCard.TryGetValue(cardId, out var keys))
                foreach (var key in keys.Keys)
                    if (key.Grade != 0) recordCount++;
        }

        using var pw = new PacketWriter(16 + 8 * recordCount);
        var lenPos = pw.Reserve(4);
        var start = pw.Length;
        pw.WriteUInt32(actorHandle);
        pw.WriteUInt16((ushort)recordCount);
        foreach (var cardId in order)
        {
            byte cardState = state[cardId];
            byCard.TryGetValue(cardId, out var byKey);
            WriteListRecord(pw, cardId, cardState, byKey?.GetValueOrDefault(((byte)0, (byte)0)) ?? 0, grade: 0, kind: 0);
            WriteListRecord(pw, cardId, cardState, byKey?.GetValueOrDefault(((byte)0, (byte)1)) ?? 0, grade: 0, kind: 1);
            if (byKey is null) continue;
            foreach (var (key, n) in byKey)
                if (key.Grade != 0) WriteListRecord(pw, cardId, cardState, n, key.Grade, key.Kind);
        }
        pw.WriteAt(lenPos, (uint)(pw.Length - start));
        return pw.ToArray();
    }

 /// <summary>
 /// Writes one 2587 record: [u32 cardId][u8 state][u8 copies][u8 enforceGrade][u8 kind].
 /// </summary>
 /// <param name="pw">Writer.</param>
 /// <param name="cardId">Card id.</param>
 /// <param name="state">Registry state of the card.</param>
 /// <param name="copies">Number of copies.</param>
 /// <param name="grade">Enforce grade.</param>
 /// <param name="kind">Kind of copy: 1 for a copy announced by the put reply, otherwise 0.</param>
    private static void WriteListRecord(PacketWriter pw, uint cardId, byte state, int copies, byte grade, byte kind)
    {
        pw.WriteUInt32(cardId);
        pw.WriteByte(state);                                 
        pw.WriteByte((byte)Math.Clamp(copies, 0, 255));     
        pw.WriteByte(grade);                                 
        pw.WriteByte(kind);                                  
    }

 /// <summary>
 /// Builds 2588 AnsRegistCardBook, which announces that a card has reached the bag and is now registered.
 /// </summary>
 /// <remarks>
 /// <para>
 /// Frame is 14 bytes: [u32 10][u16 1][u32 cardId][u8 2][u8 0][u8 0][u8 0]. The state byte is always 2.
 /// </para>
 /// <para>
 /// Sent when a card reaches the bag, from each arrival path (trade, corpse pickup, socket extraction, composition, quest reward), and at world entry for MVP cards. It is not sent when a card is put into the book.
 /// </para>
 /// </remarks>
 /// <param name="cardId">Card id.</param>
    public static byte[] BuildRegistCardBook2588(uint cardId)
    {
        using var pw = new PacketWriter(32);
        var lenPos = pw.Reserve(4);
        var start = pw.Length;
        pw.WriteUInt16(1);        
        pw.WriteUInt32(cardId);
        pw.WriteByte(2);          
        pw.WriteByte(0);
        pw.WriteByte(0);
        pw.WriteByte(0);
        pw.WriteAt(lenPos, (uint)(pw.Length - start));
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2791 AnsCardBookPut, the answer to a put request.
 /// </summary>
 /// <remarks>
 /// Fixed 6 bytes: [u16 result][u32 cardId]. Result 0 is success, and the card id is the template id of the item that left the bag. A refusal carries card id 0.
 /// </remarks>
 /// <param name="result">Result code. 0 is success.</param>
 /// <param name="cardId">Card id, or 0 for a refusal.</param>
    public static byte[] BuildCardBookPut2791(ushort result, uint cardId)
    {
        using var pw = new PacketWriter(8);
        pw.WriteUInt16(result);
        pw.WriteUInt32(cardId);
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2616 AckCardComposition, the answer to a composition request.
 /// </summary>
 /// <remarks>
 /// Fixed 10 bytes: [u16 result][u32 sourceCardId][u32 resultCardId]. On success, the result card is the composition result of the source card. A failure has a nonzero result and zero ids.
 /// </remarks>
 /// <param name="result">Result code.</param>
 /// <param name="sourceCardId">Source card id.</param>
 /// <param name="resultCardId">Result card id.</param>
    public static byte[] BuildCardComposition2616(ushort result, uint sourceCardId, uint resultCardId)
    {
        using var pw = new PacketWriter(16);
        pw.WriteUInt16(result);
        pw.WriteUInt32(sourceCardId);
        pw.WriteUInt32(resultCardId);
        return pw.ToArray();
    }
}
