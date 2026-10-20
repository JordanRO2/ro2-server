using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The appearance change bought with an Appearance Change Ticket: the request, the answer, and the rules that decide what is applied.
/// </summary>
/// <remarks>
/// <para>
/// Request (1705 ReqAppearanceChange, 15 bytes): the appearance block of the character create packet. Its builder overwrites create[0] with the character's own value, so create[0] cannot change through this request.
/// </para>
/// <para>
/// Answer (3046 AnsAppearanceChange, compact, 31 bytes): the 15-byte appearance block, next to the actor handle and a u16 result.
/// </para>
/// <para>
/// A successful change is applied to the actor named by the handle. A non-zero result is shown to the player as a message, and nothing else in the answer is read.
/// </para>
/// </remarks>
internal static class AppearanceChangePackets
{
 /// <summary>Message tag of the 3046 answer, in its blob.</summary>
    internal const ushort MsgTagAnsAppearanceChange = 58526;

 /// <summary>Size of the appearance block: the body of the request and the appearance field of the answer.</summary>
    internal const int AppearanceLength = 15;

 /// <summary>Body length of the answer: a compact header plus a 29-byte blob.</summary>
    internal const int AnswerBodyLength = 31;

 /// <summary>Result code: the change was applied.</summary>
    internal const ushort ResultApplied = 0;

 /// <summary>Result code: no Appearance Change Ticket in the bag.</summary>
    internal const ushort ResultNoTicket = 23060;

 /// <summary>Result code: the change would alter the gender while a gender-specific item is worn.</summary>
    internal const ushort ResultGenderSpecificItemWorn = 23061;

 /// <summary>Item type of the Appearance Change Tickets.</summary>
    internal const byte AppearanceTicketItemType = 72;

 /// <summary>Source code of the inventory update that consumes a ticket (2329 blob+21).</summary>
    internal const byte UseSourceAppearanceTicket = 3;

 /// <summary>
 /// Index of the gender byte in the appearance block, stored as <c>Character.Race</c>.
 /// </summary>
 /// <remarks>
 /// Worn items with a sex requirement must match the gender in this byte. Index 0 is not the gender.
 /// </remarks>
    internal const int GenderIndex = 1;

 /// <summary>
 /// Builds the 3046 AnsAppearanceChange answer.
 /// </summary>
 /// <remarks>
 /// Body: compact header, then a 29-byte blob with the actor handle, the 15-byte appearance and the result.
 /// </remarks>
 /// <param name="pcHandle">Handle of the actor.</param>
 /// <param name="appearance">Appearance block.</param>
 /// <param name="result">Result code.</param>
    internal static byte[] BuildAnsAppearanceChange3046(uint pcHandle, ReadOnlySpan<byte> appearance, ushort result)
    {
        if (appearance.Length != AppearanceLength)
            throw new ArgumentException($"3046 carries the {AppearanceLength}-byte appearance block; got {appearance.Length}.", nameof(appearance));
        var block = appearance.ToArray();
        using var pw = new PacketWriter(AnswerBodyLength);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagAnsAppearanceChange); 
            p.WriteUInt32(0);                         
            p.WriteUInt32(pcHandle);                  
            p.WriteBytes(block);                      
            p.WriteUInt16(result);                    
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the stored appearance block after a change: the request applied over the stored block.
 /// </summary>
 /// <remarks>
 /// create[0] keeps its stored value, and the bytes from index 15 on are kept unchanged. A row without a stored block, or with a shorter one, gets a block as long as the longer of the two.
 /// </remarks>
 /// <param name="storedRaw">Stored appearance block, or null.</param>
 /// <param name="storedCreate0">Stored value of create[0].</param>
 /// <param name="request">Requested appearance block.</param>
    internal static byte[] ApplyToCreateBlock(byte[]? storedRaw, byte storedCreate0, ReadOnlySpan<byte> request)
    {
        if (request.Length != AppearanceLength)
            throw new ArgumentException($"1705 carries the {AppearanceLength}-byte appearance block; got {request.Length}.", nameof(request));
        int length = Math.Max(storedRaw?.Length ?? 0, AppearanceLength);
        var raw = new byte[length];
        storedRaw?.CopyTo(raw, 0);
        request.CopyTo(raw);
        if (storedRaw is { Length: > 0 }) raw[0] = storedRaw[0];
        else if (storedCreate0 != 0) raw[0] = storedCreate0;
        return raw;
    }

 /// <summary>
 /// The 15 bytes a refusal answer carries: the stored appearance, or the request when no stored block of that length exists.
 /// </summary>
 /// <param name="storedRaw">Stored appearance block, or null.</param>
 /// <param name="request">Requested appearance block.</param>
    internal static byte[] RefusalBlock(byte[]? storedRaw, ReadOnlySpan<byte> request)
    {
        if (storedRaw is { Length: >= AppearanceLength }) return storedRaw.AsSpan(0, AppearanceLength).ToArray();
        return request.ToArray();
    }

 /// <summary>
 /// Whether a change must be refused because it alters the gender while a worn item is made for one gender only.
 /// </summary>
 /// <remarks>
 /// Items with a sex requirement value 1 or 2 block the change when the new gender differs from the required one. A requirement value 3 applies to both genders, and 0 applies to none.
 /// </remarks>
 /// <param name="oldGender">Current gender value.</param>
 /// <param name="newGender">Requested gender value.</param>
 /// <param name="wornRequireSex">Sex requirement of each worn item.</param>
    internal static bool GenderChangeBlockedByWornItems(byte oldGender, byte newGender, IEnumerable<byte> wornRequireSex)
    {
        if (oldGender == newGender) return false;
        foreach (var sex in wornRequireSex)
            if (sex is 1 or 2 && sex != newGender) return true;
        return false;
    }
}
