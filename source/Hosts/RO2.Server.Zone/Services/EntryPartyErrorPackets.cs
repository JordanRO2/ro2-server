using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>S2C 2969 NfyEntryPartyError: an error notice about joining a tournament-room match.</summary>
/// <remarks>
/// <para>
/// Body (NONE family): a raw <c>u16</c> error code, with no length prefix, msgTag or inner header.
/// </para>
/// <para>
/// The client maps the code to a notice text and shows nothing for codes it does not list.
/// </para>
/// <para>
/// The server sends this in reply to a join answer (1658) for a match that is no longer pending.
/// </para>
/// </remarks>
public static class EntryPartyErrorPackets
{
 /// <summary>Body length: one u16.</summary>
    internal const int BodyLength = 2;

 /// <summary>Code sent after an accepted join (01) that has no match to join.</summary>
    internal const ushort CodeStaleAccept = 24343;

 /// <summary>Code sent after a declined join (00) that has no match to decline.</summary>
    internal const ushort CodeStaleDecline = 24345;

 /// <summary>The code for a join answer that no pending match can take, by its answer byte.</summary>
    internal static ushort StaleAnswerCode(bool accept) => accept ? CodeStaleAccept : CodeStaleDecline;

 /// <summary>The 2969 body: <c>[u16 code]</c>.</summary>
    public static byte[] Build(ushort code)
    {
        var body = new byte[BodyLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, code);
        return body;
    }
}
