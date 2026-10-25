using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The party-matching apply board: the postings made with 1458 ReqPartyMatchingApplyMsg, and the listing returned
/// for 1460 ReqPartyMatchingSearchApply as 2622 AnsPartyApplySearch. It works like the recruit board,
/// <see cref="PartyMatchingBoard"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each character has one posting, which a re-post replaces. The board stores only what the poster typed. The
/// name, class and GID are read from the poster's live state when the list is built, and a posting whose poster
/// is no longer online is dropped. A player's own posting is removed by 1459 kind 2.
/// </para>
/// <para>
/// The board is a process-wide singleton held in memory, because postings belong to online players.
/// </para>
/// </remarks>
public sealed class PartyApplyBoard
{
    public static readonly PartyApplyBoard Instance = new();

 /// <summary>Used by the handlers through <see cref="Instance"/>. Tests create their own board.</summary>
    internal PartyApplyBoard() { }

 /// <summary>Size of the 1458 body in bytes.</summary>
    public const int ApplyMsgBytes = 49;

 /// <summary>Size of the 1460 body in bytes.</summary>
    public const int SearchBytes = 9;

 /// <summary>
 /// Length of the title and name buffers in wide characters. The client writes at most 20 characters followed by a NUL.
 /// </summary>
    public const int TextChars = 21;

 /// <summary>
 /// Rows per 2622 page. The result window has 10 row controls, so a page never carries more than 10.
 /// </summary>
    public const int PageSize = 10;

 /// <summary>Number of difficulty levels the client's search form offers (All, Easy, Normal, Hard).</summary>
    public const int DifficultyCount = 4;

 /// <summary>
 /// Category ids of the client's Find_Party_Category table. Every viewer builds its category list from that table,
 /// so a row whose category is missing from it cannot be drawn.
 /// </summary>
 /// <remarks>
 /// This is the client's own table, not <c>GameDataStore.FindPartyCategory</c>. If a client build adds a category,
 /// this list must follow it.
 /// </remarks>
    public static ReadOnlySpan<byte> ClientCategoryIds => [0, 1, 2, 3];

 /// <param name="CharacterId">Character that made the posting.</param>
 /// <param name="Category">Category id.</param>
 /// <param name="RoleFlags">Role flags.</param>
 /// <param name="Difficulty">Difficulty index.</param>
 /// <param name="Destination">Destination id.</param>
 /// <param name="Title">Title, up to its NUL terminator.</param>
    public sealed record Posting(uint CharacterId, byte Category, byte RoleFlags, byte Difficulty, uint Destination,
        string Title);

 /// <summary>The search filter carried by a 1460 request.</summary>
    public readonly record struct SearchFilter(byte Category, byte RoleFlags, byte Difficulty, ushort Page,
        uint Destination);

    private readonly ConcurrentDictionary<uint, Posting> _byCharacter = new();

 /// <summary>Number of postings currently held.</summary>
    public int Count => _byCharacter.Count;

 /// <summary>
 /// Parses a 1458 body: category (u8), role flags (u8), difficulty (u8), destination (u32) and the wide-character title.
 /// </summary>
 /// <remarks>
 /// The client refuses to send a posting with no role selected, so a body with no role flags is not expected.
 /// </remarks>
    public static bool TryParseApplyMsg(ReadOnlySpan<byte> body, out byte category, out byte roleFlags,
        out byte difficulty, out uint destination, out string title)
    {
        category = roleFlags = difficulty = 0;
        destination = 0;
        title = string.Empty;
        if (body.Length < ApplyMsgBytes) return false;

        category = body[0];
        roleFlags = body[1];
        difficulty = body[2];
        destination = BinaryPrimitives.ReadUInt32LittleEndian(body[3..]);
        title = ReadWide(body.Slice(7, TextChars * 2));
        return true;
    }

 /// <summary>
 /// Parses a 1460 body: category (u8), role flags (u8), difficulty (u8), page (u16) and destination (u32).
 /// An all-zero filter with page 1 means "All" for every field.
 /// </summary>
    public static bool TryParseSearch(ReadOnlySpan<byte> body, out SearchFilter filter)
    {
        filter = default;
        if (body.Length < SearchBytes) return false;
        filter = new SearchFilter(body[0], body[1], body[2], BinaryPrimitives.ReadUInt16LittleEndian(body[3..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[5..]));
        return true;
    }

 /// <summary>
 /// Whether every viewer can draw a row with this category and difficulty. A category outside
 /// <see cref="ClientCategoryIds"/> or a difficulty at or above <see cref="DifficultyCount"/> cannot be drawn by the client.
 /// </summary>
    public static bool IsListable(byte category, byte difficulty)
        => difficulty < DifficultyCount && ClientCategoryIds.Contains(category);

 /// <summary>
 /// Posts, or replaces, <paramref name="characterId"/>'s notice from a 1458 body.
 /// </summary>
 /// <returns>The stored posting, or null when the body is too short or holds a category or difficulty that cannot be listed.</returns>
    public Posting? Post(uint characterId, ReadOnlySpan<byte> applyMsgBody)
    {
        if (!TryParseApplyMsg(applyMsgBody, out var category, out var roleFlags, out var difficulty,
                out var destination, out var title))
            return null;
        if (!IsListable(category, difficulty))
            return null;
        var posting = new Posting(characterId, category, roleFlags, difficulty, destination, title);
        _byCharacter[characterId] = posting;
        return posting;
    }

    public bool Remove(uint characterId) => _byCharacter.TryRemove(characterId, out _);

 /// <summary>Removes <paramref name="posting"/> when it is still the character's current one.</summary>
    public bool Remove(Posting posting)
        => _byCharacter.TryRemove(new KeyValuePair<uint, Posting>(posting.CharacterId, posting));

    public IReadOnlyList<Posting> Snapshot() => _byCharacter.Values.ToList();

 /// <summary>
 /// Whether <paramref name="p"/> matches <paramref name="f"/>. Item 0 of each choice list means "any", and role
 /// flags of 0 mean any role.
 /// </summary>
    public static bool Matches(Posting p, SearchFilter f)
        => (f.Category == 0 || p.Category == f.Category)
        && (f.RoleFlags == 0 || (p.RoleFlags & f.RoleFlags) != 0)
        && (f.Difficulty == 0 || p.Difficulty == f.Difficulty)
        && (f.Destination == 0 || p.Destination == f.Destination);

 /// <summary>
 /// Builds the listing rows from the board and the posters' live state. The name, class and GID are read now, so a
 /// row can be whispered or invited like a party member.
 /// </summary>
 /// <remarks>
 /// A posting whose poster is no longer online is removed from the board.
 /// </remarks>
    public List<PartyApplyListing> ResolveListings(Func<uint, PlayerState?> lookupByCharacterId, SearchFilter filter)
    {
        var rows = new List<PartyApplyListing>();
        foreach (var posting in Snapshot())
        {
            var poster = lookupByCharacterId(posting.CharacterId);
            if (poster == null)
            {
                
                Remove(posting);
                continue;
            }
            if (!Matches(posting, filter)) continue;

            rows.Add(new PartyApplyListing(posting.Category, posting.RoleFlags, posting.Difficulty,
                PartyMatchingDetailPackets.MemberOf(poster).Job, posting.Destination, poster.Name, posting.Title,
                PartyAnswerPackets.MemberGid(poster)));
        }
        return rows;
    }

 /// <summary>
 /// Orders the rows and selects a page. Pages are 1-based. The reply always reports at least one page, and a page
 /// beyond the last is answered with the last page.
 /// </summary>
    public static (ushort Page, ushort Pages, List<PartyApplyListing> Rows) Page(
        IEnumerable<PartyApplyListing> rows, ushort requestedPage)
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
        
        while (chars < TextChars - 1 && chars * 2 + 1 < buffer.Length
               && (buffer[chars * 2] != 0 || buffer[chars * 2 + 1] != 0))
            chars++;
        return Encoding.Unicode.GetString(buffer[..(chars * 2)]);
    }
}
