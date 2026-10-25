using System.Buffers.Binary;
using System.Text;
using RO2.Network.Packets;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Packets for the party-matching detail flow: 1461 (ReqPartyMatchingDetail), 2623 (AnsPartyMatchingDetail,
/// the member list of a recruit notice), 2624 (AnsPartyMatchingError), and the poster lookup shared with 1462.
/// </summary>
public static class PartyMatchingDetailPackets
{
 /// <summary>Body length of a 1461 request, in bytes.</summary>
    public const int RequestBytes = 12;

 /// <summary>Length of the 2623 header: party handle, member count and poster GID.</summary>
    public const int AnswerHeaderBytes = 13;

 /// <summary>Length of one member record in a 2623 answer, in bytes.</summary>
    public const int MemberRecordBytes = 49;

 /// <summary>Length of a member record's name buffer, in wide characters, including the terminator.</summary>
    public const int NameChars = 21;

 /// <summary>Mark selector value that hides the member's mark.</summary>
    public const byte MarkHidden = 2;

    
    
 /// <summary>Operation code of a 2624 error for a 1461 failure.</summary>
    public const ushort ErrorOpDetail = 6;

 /// <summary>Operation code of a 2624 error for a 1462 failure.</summary>
    public const ushort ErrorOpApply = 7;

 /// <summary>Error code: no recruit listing was found.</summary>
    public const ushort CodeNoListing = 103;

 /// <summary>Error code: the applicant is already in a party or raid.</summary>
    public const ushort CodeInAnotherParty = 105;

 /// <summary>Error code: the applicant already belongs to the selected party.</summary>
    public const ushort CodeAlreadyInParty = 106;

 /// <summary>Character details shown in a 2623 member record.</summary>
 /// <param name="Level">Level shown when <paramref name="MasterLevel"/> is zero.</param>
 /// <param name="Job">Job (class) ID.</param>
 /// <param name="MasterLevel">Master level. When non-zero, it is shown in place of the level.</param>
 /// <param name="Name">Character name.</param>
    public readonly record struct Member(byte Level, byte Job, byte MasterLevel, string Name);

 /// <summary>
 /// Builds the member view of a player from the same sources as the party roster, so the detail and
 /// party windows show the same values.
 /// </summary>
 /// <remarks>
 /// The master level is taken from the player's professional-job level, not from <c>Character.MasterLevel</c>.
 /// </remarks>
    public static Member MemberOf(PlayerState p)
    {
        var c = p.Character;
        return new Member(
            p.Level,
            c != null ? WorldEntryService.CombatJobByte(c) : p.Job,
            c != null ? CharacterSerializer.WireProJob(c).Level : (byte)0,
            p.Name);
    }

 /// <summary>Parses a 1461 body.</summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> body, out uint partyHandle, out ulong posterGid)
    {
        partyHandle = 0;
        posterGid = 0;
        if (body.Length < RequestBytes) return false;
        partyHandle = BinaryPrimitives.ReadUInt32LittleEndian(body);
        posterGid = BinaryPrimitives.ReadUInt64LittleEndian(body[4..]);
        return true;
    }

 /// <summary>
 /// Finds the online player whose recruit notice a 1461 or 1462 names, matched by GID.
 /// </summary>
    public static PlayerState? FindPoster(IEnumerable<PlayerState> players,
        IEnumerable<PartyMatchingBoard.Posting> postings, ulong posterGid)
    {
        if (posterGid == 0) return null;
        var posters = postings.Select(p => p.CharacterId).ToHashSet();
        return players.FirstOrDefault(p => posters.Contains(p.CharacterId) && PartyAnswerPackets.MemberGid(p) == posterGid);
    }

 /// <summary>
 /// Returns the characters a 2623 lists for a poster: the poster alone when not in a party, otherwise
 /// every party member in roster order.
 /// </summary>
    public static List<PlayerState> DetailMembers(PlayerState poster, PartyState? party,
        Func<uint, PlayerState?> knownMember)
    {
        if (party == null) return [poster];
        var members = new List<PlayerState>();
        foreach (var memberId in party.MemberSessionIds.ToArray())
            if (knownMember(memberId) is { } member)
                members.Add(member);
        if (members.Count == 0) members.Add(poster);
        return members;
    }

 /// <summary>
 /// Builds a 2623 (AnsPartyMatchingDetail) answer. The body is a compact-length-prefixed blob:
 /// <code>
 /// +0 u32 party handle
 /// +4 u8 member count
 /// +5 u64 poster GID
 /// +13 n x 49-byte member records:
 /// +0 u8 level
 /// +2 u8 class
 /// +4 wchar_t[21] name
 /// +46 u8 master level
 /// +48 u8 mark (see <see cref="MarkHidden"/>)
 /// </code>
 /// </summary>
    public static byte[] BuildAnsPartyMatchingDetail(uint partyHandle, ulong posterGid, IReadOnlyList<Member> members)
    {
        var count = Math.Min(members.Count, byte.MaxValue);
        using var blob = new PacketWriter(AnswerHeaderBytes + MemberRecordBytes * count);
        blob.WriteUInt32(partyHandle);
        blob.WriteByte((byte)count);
        blob.WriteUInt64(posterGid);
        for (var i = 0; i < count; i++)
        {
            var m = members[i];
            blob.WriteByte(m.Level);                    
            blob.WriteByte(0);                          
            blob.WriteByte(m.Job);                      
            blob.WriteByte(0);                          
            blob.WriteBytes(WideTerminated(m.Name, NameChars)); 
            blob.WriteByte(m.MasterLevel);              
            blob.WriteByte(0);                          
            blob.WriteByte(MarkHidden);                 
        }

        using var pw = new PacketWriter(blob.Length + 9);
        pw.WriteCompactLength(blob.Length);
        pw.WriteBytes(blob.GetWrittenSpan());
        return pw.ToArray();
    }

 /// <summary>Builds a 2624 (AnsPartyMatchingError) answer: operation code and error code, 4 bytes.</summary>
    public static byte[] BuildError(ushort op, ushort code)
        => PartyStatePackets.BuildAnsPartyMatchingError(op, code);

 /// <summary>
 /// Encodes a string as UTF-16LE in a zero-filled buffer of <paramref name="bufferChars"/> characters,
 /// always leaving room for a terminator.
 /// </summary>
    internal static byte[] WideTerminated(string? value, int bufferChars)
    {
        var buf = new byte[bufferChars * 2];
        if (!string.IsNullOrEmpty(value))
        {
            var take = Math.Min(value.Length, bufferChars - 1);
            Encoding.Unicode.GetBytes(value.AsSpan(0, take), buf.AsSpan(0, take * 2));
        }
        return buf;
    }
}
