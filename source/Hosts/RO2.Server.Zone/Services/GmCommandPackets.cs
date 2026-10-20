using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2893 AnsGmCommand: the outcome line of a GM command. The client shows the message in its own language.
/// </summary>
/// <remarks>
/// <para>
/// The body is <c>[u32 innerLen][u16 code][u16 n][n x UTF-16LE argument]</c>, with no innerSize/msgTag header in
/// front of the length. <c>innerLen = 4 + 2n</c> and the characters carry no terminator (<c>n</c> bounds them).
/// </para>
/// <para>
/// Codes 1 to 30 select a message from the client's string table (string <see cref="MessageStringBase"/> + code),
/// and the argument fills its @1 placeholder; the line is shown in the system chat category. Code 31 triggers a
/// chat-filter UI action instead of a message, and 0 or anything above 31 is dropped. The template texts are in
/// data/strings/string_etc.json.
/// </para>
/// <para>
/// This server sends 2893 as the answer to its own '/' chat commands, for the outcomes that one of these client
/// strings names.
/// </para>
/// </remarks>
internal static class GmCommandPackets
{
 /// <summary>First id of the message block: the client shows string <c>MessageStringBase + code</c>.</summary>
    public const uint MessageStringBase = 2_000_000_160;

 /// <summary>Lowest message code (0 is dropped by the client).</summary>
    public const ushort FirstMessageCode = 1;

 /// <summary>Highest message code (31 is a chat-filter UI action, not a message).</summary>
    public const ushort LastMessageCode = 30;

 /// <summary>2000000161 "Not Founded PC. Name: @1".</summary>
    public const ushort PcNotFoundByName = 1;

 /// <summary>2000000164 "GM grade : @1".</summary>
    public const ushort GmGrade = 4;

 /// <summary>2000000166 "Not Founded Item. Index: @1".</summary>
    public const ushort ItemNotFound = 6;

 /// <summary>2000000170 "PC Location: @1".</summary>
    public const ushort PcLocation = 10;

 /// <summary>2000000179 "Incorrect Speed : @1".</summary>
    public const ushort IncorrectSpeed = 19;

 /// <summary>2000000185 "Invalid Map ID : @1".</summary>
    public const ushort InvalidMapId = 25;

 /// <summary>2000000187 "Invalid Coordinates : @1".</summary>
    public const ushort InvalidCoordinates = 27;

 /// <summary>Size of the u32 inner-length prefix that opens the body.</summary>
    private const int InnerLengthPrefixSize = sizeof(uint);

 /// <summary>Size of the fixed inner header: the u16 code and the u16 argument length.</summary>
    private const int InnerHeaderSize = 2 * sizeof(ushort);

 /// <summary>
 /// Builds a 2893 body: <c>[u32 4 + 2n][u16 code][u16 n][n x UTF-16LE]</c>, where the string is the @1 argument
 /// of the message for <paramref name="code"/>.
 /// </summary>
 /// <exception cref="ArgumentOutOfRangeException">
 /// <paramref name="code"/> is not a message code (1 to 30).
 /// </exception>
    public static byte[] BuildAnsGmCommand2893(ushort code, string argument)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(code, FirstMessageCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(code, LastMessageCode);
        ArgumentNullException.ThrowIfNull(argument);
        // The argument length is a u16 count of UTF-16 code units.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(argument.Length, ushort.MaxValue);

        var chars = Encoding.Unicode.GetBytes(argument);
        using var w = new PacketWriter(InnerLengthPrefixSize + InnerHeaderSize + chars.Length);
        w.WriteUInt32((uint)(InnerHeaderSize + chars.Length));   // inner length (everything after this prefix)
        w.WriteUInt16(code);                                     // inner+0 message code
        w.WriteUInt16((ushort)argument.Length);                  // inner+2 argument length, UTF-16 code units
        w.WriteBytes(chars);                                     // inner+4 argument, no terminator
        return w.ToArray();
    }
}
