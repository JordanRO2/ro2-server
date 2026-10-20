using System.Buffers.Binary;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Packets for applying to a recruit notice: 1462 (ReqPartyMatchingWhisper) and 2625
/// (AnsPartyMatchingWhisper), the invite request sent to the poster.
/// </summary>
public static class PartyMatchingWhisperPackets
{
 /// <summary>Body length of a 1462 request, in bytes.</summary>
    public const int RequestBytes = 13;

 /// <summary>Body length of a 2625 answer, in bytes.</summary>
    public const int AnswerBytes = 52;

 /// <summary>Length of the applicant name buffer in a 2625 answer, in wide characters, including the terminator.</summary>
    public const int NameChars = 21;

    
    
    
    public const byte RoleTank = 1;
    public const byte RoleDealer = 2;
    public const byte RoleHealer = 4;
    public const byte RoleMask = RoleTank | RoleDealer | RoleHealer;

 /// <summary>A parsed 1462 request.</summary>
 /// <param name="PartyHandle">Party handle of the recruit notice.</param>
 /// <param name="PosterGid">GID of the recruit notice's poster.</param>
 /// <param name="Roles">Role bits of the applicant.</param>
    public readonly record struct Request(uint PartyHandle, ulong PosterGid, byte Roles);

 /// <summary>Parses a 1462 body.</summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> body, out Request request)
    {
        request = default;
        if (body.Length < RequestBytes) return false;
        request = new Request(
            BinaryPrimitives.ReadUInt32LittleEndian(body),
            BinaryPrimitives.ReadUInt64LittleEndian(body[4..]),
            (byte)(body[12] & RoleMask));
        return true;
    }

 /// <summary>
 /// Returns the error code a 1462 is refused with, or 0 when the application proceeds.
 /// </summary>
    public static ushort RefusalCode(PartyState? applicantParty, PartyState? posterParty)
    {
        if (applicantParty == null) return 0;
        return posterParty != null && posterParty.PartyId == applicantParty.PartyId
            ? PartyMatchingDetailPackets.CodeAlreadyInParty
            : PartyMatchingDetailPackets.CodeInAnotherParty;
    }

 /// <summary>
 /// Builds a 2625 (AnsPartyMatchingWhisper) answer for the poster about the applicant. Fixed 52-byte body:
 /// <code>
 /// +4 u8 applicant level
 /// +6 u8 applicant class
 /// +8 u8 role bits
 /// +9 u8 applicant master level
 /// +10 wchar_t[21] applicant name
 /// </code>
 /// </summary>
    public static byte[] BuildAnsPartyMatchingWhisper(PartyMatchingDetailPackets.Member applicant, byte roles)
    {
        using var pw = new PacketWriter(AnswerBytes);
        pw.WriteUInt32(0);                        
        pw.WriteByte(applicant.Level);            
        pw.WriteByte(0);                          
        pw.WriteByte(applicant.Job);              
        pw.WriteByte(0);                          
        pw.WriteByte((byte)(roles & RoleMask));   
        pw.WriteByte(applicant.MasterLevel);      
        pw.WriteBytes(PartyMatchingDetailPackets.WideTerminated(applicant.Name, NameChars)); 
        return pw.ToArray();
    }
}
