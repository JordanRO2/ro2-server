using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace RO2.Server.Zone.Services;

/// <summary>
/// One row of the 2621 (AnsPartyMemberSearch) listing: a fixed 103-byte record.
/// </summary>
/// <code>
/// +0 u8 member count shown as a number, minimum 1
/// +1 u8 category 1 Quest, 2 Party, 3 Raid, 4 PvP; 0 = none ("All")
/// +2 u8 difficulty 0 All, 1 Easy, 2 Normal, 3 Hard
/// +3 u32 party handle low byte 0x15 (group tag); 0 when the poster has no party
/// +7 u32 destination destination id, scoped to the category's map
/// +11 u64 poster GID the whisper / invite target
/// +19 wchar_t[21] poster name
/// +61 wchar_t[21] title read up to its NUL
/// </code>
public readonly record struct PartyMatchingListing(
    byte MemberCount, byte Category, byte Difficulty, uint PartyHandle, uint Destination,
    ulong PosterGid, string PosterName, string Title);

/// <summary>
/// Recruit board for party matching. Postings are created by 1554 (ReqPartyMatchingInviteMsg) and
/// returned to searches as 2621 (AnsPartyMemberSearch) in answer to 1560 (ReqPartyMatchingSearchInvite).
/// </summary>
public sealed class PartyMatchingBoard
{
    public static readonly PartyMatchingBoard Instance = new();

    /// <summary>Handlers use <see cref="Instance"/>; tests create their own board.</summary>
    internal PartyMatchingBoard() { }

    /// <summary>Length of a 1554 body, in bytes.</summary>
    public const int InviteMsgBytes = 48;

    /// <summary>Length of a 1560 body, in bytes.</summary>
    public const int SearchBytes = 8;

    /// <summary>Length of the title and name buffers, in wide characters, including the terminator.</summary>
    public const int TextChars = 21;

    /// <summary>Maximum number of rows per 2621 page; matches the client's result window of 10 row controls.</summary>
    public const int PageSize = 10;

    // 1554 body offsets.
    private const int InviteCategoryOff = 0;
    private const int InviteDifficultyOff = 1;
    private const int InviteDestinationOff = 2;
    private const int InviteTitleOff = 6;

    // 1560 body offsets.
    private const int SearchDifficultyOff = 0;
    private const int SearchCategoryOff = 1;
    private const int SearchPageOff = 2;
    private const int SearchDestinationOff = 4;

    /// <summary>Filter value that matches every category, difficulty or destination (the "All" combo item).</summary>
    private const int FilterAny = 0;

    /// <summary>A recruit notice posted by a character.</summary>
    /// <param name="CharacterId">Poster's character ID.</param>
    /// <param name="Category">Category, from the 1554 body.</param>
    /// <param name="Difficulty">Difficulty, from the 1554 body.</param>
    /// <param name="Destination">Destination, from the 1554 body.</param>
    /// <param name="Title">Title, decoded up to its terminator.</param>
    public sealed record Posting(uint CharacterId, byte Category, byte Difficulty, uint Destination, string Title);

    /// <summary>Search filter carried by a 1560 request.</summary>
    public readonly record struct SearchFilter(byte Difficulty, byte Category, ushort Page, uint Destination);

    private readonly ConcurrentDictionary<uint, Posting> _byCharacter = new();

    /// <summary>Number of postings currently held.</summary>
    public int Count => _byCharacter.Count;

    /// <summary>
    /// Parses a 1554 body. Layout (48 bytes):
    /// <code>
    /// +0 u8 category
    /// +1 u8 difficulty
    /// +2 u32 destination
    /// +6 wchar_t[21] title
    /// </code>
    /// </summary>
    public static bool TryParseInviteMsg(ReadOnlySpan<byte> body, out byte category, out byte difficulty,
        out uint destination, out string title)
    {
        category = difficulty = 0;
        destination = 0;
        title = string.Empty;
        if (body.Length < InviteMsgBytes) return false;

        category = body[InviteCategoryOff];
        difficulty = body[InviteDifficultyOff];
        destination = BinaryPrimitives.ReadUInt32LittleEndian(body[InviteDestinationOff..]);
        title = ReadWide(body.Slice(InviteTitleOff, TextChars * 2));
        return true;
    }

    /// <summary>
    /// Parses a 1560 body. Layout (8 bytes):
    /// <code>
    /// +0 u8 difficulty 0 = any
    /// +1 u8 category 0 = any
    /// +2 u16 page
    /// +4 u32 destination 0 = any
    /// </code>
    /// </summary>
    public static bool TryParseSearch(ReadOnlySpan<byte> body, out SearchFilter filter)
    {
        filter = default;
        if (body.Length < SearchBytes) return false;
        filter = new SearchFilter(body[SearchDifficultyOff], body[SearchCategoryOff],
            BinaryPrimitives.ReadUInt16LittleEndian(body[SearchPageOff..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[SearchDestinationOff..]));
        return true;
    }

    /// <summary>
    /// Posts or replaces a character's recruit notice from a 1554 body.
    /// </summary>
    /// <returns>The stored posting, or null when the body is too short.</returns>
    public Posting? Post(uint characterId, ReadOnlySpan<byte> inviteMsgBody)
    {
        if (!TryParseInviteMsg(inviteMsgBody, out var category, out var difficulty, out var destination, out var title))
            return null;
        var posting = new Posting(characterId, category, difficulty, destination, title);
        _byCharacter[characterId] = posting;
        return posting;
    }

    public bool Remove(uint characterId) => _byCharacter.TryRemove(characterId, out _);

    /// <summary>Removes <paramref name="posting"/> only if it is still the character's current one.</summary>
    public bool Remove(Posting posting)
        => _byCharacter.TryRemove(new KeyValuePair<uint, Posting>(posting.CharacterId, posting));

    public IReadOnlyList<Posting> Snapshot() => _byCharacter.Values.ToList();

    /// <summary>
    /// Tests whether a posting passes a search filter. A value of 0 in the filter matches any value.
    /// </summary>
    public static bool Matches(Posting p, SearchFilter f)
        => (f.Difficulty == FilterAny || p.Difficulty == f.Difficulty)
        && (f.Category == FilterAny || p.Category == f.Category)
        && (f.Destination == FilterAny || p.Destination == f.Destination);

    /// <summary>
    /// Orders matching postings by poster GID, descending, and returns the requested 1-based page.
    /// An out-of-range page returns the last page; an empty result still reports one page.
    /// </summary>
    public static (ushort Page, ushort Pages, List<PartyMatchingListing> Rows) Page(
        IEnumerable<PartyMatchingListing> rows, ushort requestedPage)
    {
        var ordered = rows.OrderByDescending(r => r.PosterGid).ToList();
        var pages = Math.Max(1, (ordered.Count + PageSize - 1) / PageSize);
        pages = Math.Min(pages, ushort.MaxValue);
        var page = Math.Clamp((int)requestedPage, 1, pages);
        var slice = ordered.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        return ((ushort)page, (ushort)pages, slice);
    }

    private static string ReadWide(ReadOnlySpan<byte> buffer)
    {
        var chars = 0;
        // At most TextChars - 1: the stored text must always fit a buffer that keeps its NUL.
        while (chars < TextChars - 1 && chars * 2 + 1 < buffer.Length
               && (buffer[chars * 2] != 0 || buffer[chars * 2 + 1] != 0))
            chars++;
        return Encoding.Unicode.GetString(buffer[..(chars * 2)]);
    }
}
