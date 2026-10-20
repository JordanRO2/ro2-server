using System.Buffers.Binary;
using System.Text;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Request body and name rules shared by 1585 ReqCanRenameGuildName (<c>GuildCanRenameHandler</c>) and the 1586 rename request (<c>GuildRenameHandler</c>).
/// </summary>
internal static class GuildRenameRules
{
 /// <summary>Result 0: the name is usable (2854), or the guild was renamed (2855).</summary>
    public const ushort Ok = 0;

 /// <summary>Result 5: another guild already has the name. The comparison is case-insensitive and includes the requester's own guild.</summary>
    public const ushort NameTaken = 5;

 /// <summary>Result 16: the name is longer than <see cref="MaxChars"/> characters.</summary>
    public const ushort NameTooLong = 16;

 /// <summary>Result 44: the name cannot be used. See <see cref="CheckFormat"/> for the rules that produce it.</summary>
    public const ushort NameNotUsable = 44;

 /// <summary>Shortest allowed guild name, in characters.</summary>
    public const int MinChars = 2;

 /// <summary>Longest allowed guild name, in characters.</summary>
    public const int MaxChars = 14;

 // Request body layout: [u32 length - 4][u64 item UID][u16 character count][count UTF-16 characters].
    private const int ItemUidOffset = 4;
    private const int CharCountOffset = ItemUidOffset + sizeof(ulong);
    private const int NameOffset = CharCountOffset + sizeof(ushort);

 /// <summary>Size of one name character on the wire (UTF-16 code unit).</summary>
    private const int BytesPerChar = sizeof(char);

 /// <summary>First code unit outside ASCII. A leading character below it must be a letter, digit or whitespace.</summary>
    private const int FirstNonAscii = 0x80;

 /// <summary>A name may not repeat one character this many times in a row.</summary>
    private const int MaxRepeatRun = 3;

 /// <summary>
 /// Parses the body shared by both requests: [u32 length - 4][u64 item UID][u16 character count][count UTF-16 characters]. Returns false when the body does not parse exactly.
 /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out ulong itemUid, out string name)
    {
        itemUid = 0;
        name = string.Empty;
        if (body.Length < NameOffset) return false;
        itemUid = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(ItemUidOffset, sizeof(ulong)));
        var chars = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(CharCountOffset, sizeof(ushort)));
        if (NameOffset + chars * BytesPerChar > body.Length) return false;
        name = Encoding.Unicode.GetString(body.Slice(NameOffset, chars * BytesPerChar));
        return true;
    }

 /// <summary>
 /// Checks the text-only name rules and returns the result code: 44 for a name shorter than <see cref="MinChars"/>, one that starts with an ASCII symbol, one containing a less-than or greater-than sign, or one that repeats a character three times in a row; 16 for a name longer than <see cref="MaxChars"/>; otherwise 0.
 /// </summary>
    public static ushort CheckFormat(string name)
    {
        if (name.Length < MinChars) return NameNotUsable;
        if (name.Length > MaxChars) return NameTooLong;
        var first = name[0];
        if (first < FirstNonAscii && !char.IsAsciiLetterOrDigit(first) && !char.IsWhiteSpace(first)) return NameNotUsable;
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '<' or '>') return NameNotUsable;
            if (i >= MaxRepeatRun - 1 && c == name[i - 1] && c == name[i - 2]) return NameNotUsable;
        }
        return Ok;
    }

 /// <summary>
 /// Returns the result code for a name check (1585). The format rules are checked first, then uniqueness.
 /// </summary>
 /// <remarks>
 /// Uniqueness uses <see cref="GuildService.IsNameTaken"/>, a case-insensitive check that includes the requester's own guild. The answer is advisory and reserves no name. <see cref="GuildService.InitAsync"/> must run first; otherwise the name registry is empty and every name reads as free.
 /// </remarks>
    public static ushort Check(string name)
    {
        var format = CheckFormat(name);
        if (format != Ok) return format;
        return GuildService.Instance.IsNameTaken(name) ? NameTaken : Ok;
    }
}
