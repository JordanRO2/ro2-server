using System.Buffers.Binary;
using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Packets for the party battle-ready check: 1500 (ReqPartyFightingTrim) starts it, 2698 and 2700 answer
/// requests, 2699 (NfyPartyFightingTrimStart) notifies members, 1501 (ReqPartyFightingTrimVote) carries a
/// vote, and 2697 (NfyPartyFightingTrimResult) reports the outcome to the initiator.
/// </summary>
public static class PartyReadyCheckPackets
{
 /// <summary>Result 0 for 2698 and 2700: nothing is shown.</summary>
    public const ushort ResultOk = 0;

 /// <summary>Result 15725 for 2700: the member has already voted.</summary>
    public const ushort ResultAlreadyVoted = 15725;

 /// <summary>Result 15727 for 2698: a check is already running.</summary>
    public const ushort ResultAlreadyChecking = 15727;

 /// <summary>Vote value for the dialog's Ready button.</summary>
    public const byte VoteReady = 1;

 /// <summary>Length of a name element in 2697, in wide characters, including the terminator.</summary>
    public const int NameChars = 21;

 /// <summary>Builds a 2698 (AnsPartyFightingTrim) answer: <c>[u16 result]</c>.</summary>
    public static byte[] BuildAnsPartyFightingTrim2698(ushort result) => U16(result);

 /// <summary>Builds a 2700 (AnsPartyFightingTrimVote) answer: <c>[u16 result]</c>.</summary>
    public static byte[] BuildAnsPartyFightingTrimVote2700(ushort result) => U16(result);

 /// <summary>
 /// Builds a 2699 (NfyPartyFightingTrimStart) notification: <c>[01 10][u16 16][u16 58432][u32 0][u64 initiator GID]</c>,
 /// 18 bytes. The GID is the one the roster records carry.
 /// </summary>
    public static byte[] BuildNfyPartyFightingTrimStart2699(ulong initiatorGid)
    {
        using var pw = new PacketWriter(18);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(PartyStatePackets.MsgTagNfyPartyFightingTrimStart); 
            p.WriteUInt32(0);                                                
            p.WriteUInt64(initiatorGid);                                     
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds a 2697 (NfyPartyFightingTrimResult) notification: <c>[u32 innerLen][u8 ready][u8 notReady][u16 n][n x wchar_t[21]]</c>,
 /// listing the names of members who are not ready, each truncated so that it keeps its terminator.
 /// </summary>
    public static byte[] BuildNfyPartyFightingTrimResult2697(byte readyCount, byte notReadyCount, IReadOnlyList<string> notReadyNames)
    {
        var n = (ushort)Math.Min(notReadyNames.Count, ushort.MaxValue);
        var body = new byte[4 + 4 + 2 * NameChars * n];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)(body.Length - 4));
        body[4] = readyCount;
        body[5] = notReadyCount;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), n);
        for (int i = 0; i < n; i++)
        {
            var name = notReadyNames[i] ?? "";
            var chars = name.Length > NameChars - 1 ? name[..(NameChars - 1)] : name;
            Encoding.Unicode.GetBytes(chars, body.AsSpan(8 + 2 * NameChars * i));
        }
        return body;
    }

    private static byte[] U16(ushort value)
    {
        var body = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(body, value);
        return body;
    }
}
