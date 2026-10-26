using System.Buffers.Binary;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire helper for the character rename requests (1564 ReqCanRenameChaName and 1563 ReqRenameChaName): the body
/// parse, the result codes, and the refusal-to-code map. The check itself is the Application
/// <see cref="RO2.Characters.Application.RenameCheckFlow"/>.
/// </summary>
/// <remarks>
/// Wire layout: a 64-bit rename scroll id, then the current name and the new name as length-prefixed UTF-16 strings.
/// Result codes: 15401 is a server-side failure of the rename, 15402 means the name is taken or too short (or no
/// scroll is present), 15403 means the name is not allowed, and 0 means the name may be used.
/// </remarks>
public static class CharacterRename
{
 /// <summary>Item type of the character name change scrolls.</summary>
    public const byte NameChangeScrollItemType = 108;

 /// <summary>Result code 15401: the rename failed on the server.</summary>
    public const uint ResultFailed = 15401;

 /// <summary>Result code 15402: the name is taken, too short, or no rename scroll is present.</summary>
    public const uint ResultRefused = 15402;

 /// <summary>Result code 15403: the name contains invalid characters, is too long, or is prohibited.</summary>
    public const uint ResultNotAllowed = 15403;

    public readonly record struct Request(ulong ScrollUid, string CurrentName, string NewName);

 /// <summary>Parses a rename request body. Returns false when the body is short or malformed.</summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out Request request)
    {
        request = default;
        if (body.Length < 8) return false;
        ulong uid = BinaryPrimitives.ReadUInt64LittleEndian(body);
        int pos = 8;
        if (!ProudWStr.TryRead(body, ref pos, out var current)) return false;
        if (!ProudWStr.TryRead(body, ref pos, out var next)) return false;
        request = new Request(uid, current, next);
        return true;
    }

 /// <summary>Maps a rename name refusal to its 2814/2813 wire result code (0 = may use, 15402, 15403).</summary>
    public static uint Code(RenameNameRefusal refusal) => refusal switch
    {
        RenameNameRefusal.None => 0,
        RenameNameRefusal.TakenOrShort => ResultRefused,
        RenameNameRefusal.NotAllowed => ResultNotAllowed,
        _ => ResultRefused,
    };
}
