using System.Buffers.Binary;
using System.Text;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Packets;

namespace RO2.Server.Chat;

/// <summary>One stored block-list entry: which character is blocked and the name it was stored with.</summary>
public sealed class BlockListEntry
{
    /// <summary>
    /// The blocked character's <c>Character.Id</c>, the key every Chat store uses. The field keeps its
    /// original name and 64-bit type so the rows written before the GID change still load: they already
    /// hold exactly this value, because the old code stored <c>target.Id</c> here and sent it as the wire
    /// uid. The wire uid is now the GID built from it (<see cref="CharacterGid"/>), so those rows need no
    /// migration, and <see cref="BlockListRepository.AddAsync"/> keeps writing the bare id.
    /// </summary>
    public ulong Uid { get; set; }

    /// <summary>The character name as last resolved from the characters collection.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>A blocked character as the client sees it: its GID (the 23506/23510 record uid) and current name.</summary>
public readonly record struct BlockedCharacter(ulong Gid, string Name);

/// <summary>Outcome of <see cref="BlockListRepository.AddAsync"/>.</summary>
public enum BlockAddOutcome
{
    /// <summary>The character was added to the list.</summary>
    Added,
    /// <summary>The character was already on the list, nothing written (23510 result 7).</summary>
    AlreadyRegistered,
    /// <summary>The list was at its defensive cap, nothing written (the protocol has no answer for this case).</summary>
    ListFull,
}

/// <summary>
/// One document per character holding its messenger block list. <see cref="Id"/> maps to Mongo
/// <c>_id</c> and is the owner's character id, the Id-&gt;_id convention KharaTraceDocument uses.
/// </summary>
public sealed class BlockListDocument
{
    /// <summary>Owner character id. Maps to Mongo _id.</summary>
    public uint Id { get; set; }

    /// <summary>Blocked characters in the order they were added (not sorted).</summary>
    public List<BlockListEntry> Entries { get; set; } = new();
}

/// <summary>
/// Mongo-backed messenger block list (<c>chat_block_lists</c> collection). A scoped DI service: it takes the
/// scope's <see cref="ICharacterRepository"/> for the name lookups.
/// </summary>
public sealed class BlockListRepository
{
    /// <summary>The Mongo collection holding one block-list document per owner character.</summary>
    public const string CollectionName = "chat_block_lists";

    /// <summary>
    /// A defensive ceiling on one owner's block list, so a scripted client cannot grow the document without
    /// bound (and so the 23506 count, written as a u16, can never approach its wrap point).
    /// No data table defines a block-list limit; 200 mirrors <see cref="FriendStore.MaxFriends"/> as a sane bound.
    /// </summary>
    public const int MaxBlocked = 200;

    private readonly IMongoCollection<BlockListDocument> _lists;
    private readonly ICharacterRepository _characters;

    public BlockListRepository(MongoDbContext context, ICharacterRepository characters)
    {
        _lists = context.GetCollection<BlockListDocument>(CollectionName);
        _characters = characters;
    }

    /// <summary>The live (not deleted) character with this name, ignoring case, or null.</summary>
    public async Task<Character?> FindCharacterByNameAsync(string name, CancellationToken ct = default)
    {
        var matches = await _characters.FindByNamesCollatedAsync(new[] { name }, ct);
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>
    /// The owner's block list as the client must see it. Each entry's uid is the blocked character's GID,
    /// built from the stored character id and the high dword of <paramref name="ownerGid"/> (the owner
    /// session's <see cref="MessengerUid"/>, see <see cref="CharacterGid"/>). Names are re-read from the
    /// characters collection on every call, so a renamed character shows its current name; an entry whose
    /// character no longer resolves keeps the name it was stored with.
    /// </summary>
    public async Task<IReadOnlyList<BlockedCharacter>> GetAsync(uint ownerId, ulong ownerGid, CancellationToken ct = default)
    {
        var doc = await _lists.Find(d => d.Id == ownerId).FirstOrDefaultAsync(ct);
        if (doc is null || doc.Entries.Count == 0) return Array.Empty<BlockedCharacter>();

        var ids = doc.Entries.Select(e => CharacterGid.CharacterIdOf(e.Uid)).Distinct().ToList();
        var current = await _characters.GetLiveByIdsAsync(ids, ct);
        var nameById = current.ToDictionary(c => c.Id, c => c.Name);
        return doc.Entries
            .Select(e =>
            {
                var id = CharacterGid.CharacterIdOf(e.Uid);
                return new BlockedCharacter(CharacterGid.Of(ownerGid, id),
                    nameById.TryGetValue(id, out var n) ? n : e.Name);
            })
            .ToList();
    }

    /// <summary>
    /// Adds <paramref name="characterId"/> to the owner's list. Writes nothing and reports
    /// <see cref="BlockAddOutcome.AlreadyRegistered"/> when that character is already on it (the client is answered
    /// with a refusal, <see cref="BlockListPackets.BuildAddBlockRefusal"/>), or
    /// <see cref="BlockAddOutcome.ListFull"/> when the list is at <see cref="MaxBlocked"/>.
    /// <para>
    /// C-H4: a single conditional <c>UpdateOne</c> replaces the former read-modify-ReplaceOne, so two
    /// concurrent blocks cannot both read the old document and lose one of the writes. The <c>$push</c> runs
    /// only while the character is not already present (ElemMatch) and the list is below the cap ($size), so
    /// it never duplicates an entry or grows past the bound. The entries are <c>{Uid, Name}</c> objects, so a
    /// plain <c>$addToSet</c> could not dedup by id alone (a renamed entry would differ); the not-present
    /// guard gives the same idempotence.
    /// </para>
    /// </summary>
    public async Task<BlockAddOutcome> AddAsync(uint ownerId, uint characterId, string name, CancellationToken ct = default)
    {
        ulong storedId = characterId;
        // $push runs only while the character is not already present (ElemMatch) and the list has room
        // (Entries.Count < MaxBlocked renders as {"Entries.<Max-1>": {$exists: false}}).
        var guard = Builders<BlockListDocument>.Filter.And(
            Builders<BlockListDocument>.Filter.Eq(d => d.Id, ownerId),
            Builders<BlockListDocument>.Filter.Not(
                Builders<BlockListDocument>.Filter.ElemMatch(d => d.Entries, e => e.Uid == storedId)),
            Builders<BlockListDocument>.Filter.Where(d => d.Entries.Count < MaxBlocked));
        var push = Builders<BlockListDocument>.Update.Push(d => d.Entries, new BlockListEntry { Uid = storedId, Name = name });

        var result = await _lists.UpdateOneAsync(guard, push, cancellationToken: ct);
        if (result.ModifiedCount > 0) return BlockAddOutcome.Added;

        // No match: no document yet, the character is already blocked, or the list is full. Re-read to tell
        // them apart - the first two have protocol answers; the cap has none.
        var doc = await _lists.Find(d => d.Id == ownerId).FirstOrDefaultAsync(ct);
        if (doc is not null)
            return doc.Entries.Any(e => CharacterGid.CharacterIdOf(e.Uid) == characterId)
                ? BlockAddOutcome.AlreadyRegistered
                : BlockAddOutcome.ListFull;

        // Truly absent: create it. The guarded upsert holds the cap even against a concurrent first-block, and
        // the loser of that create race gets a duplicate _id error, after which the document exists and one
        // conditional retry settles the outcome.
        try
        {
            await _lists.UpdateOneAsync(guard, push, new UpdateOptions { IsUpsert = true }, ct);
            return BlockAddOutcome.Added;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var retry = await _lists.UpdateOneAsync(guard, push, cancellationToken: ct);
            if (retry.ModifiedCount > 0) return BlockAddOutcome.Added;
            var after = await _lists.Find(d => d.Id == ownerId).FirstOrDefaultAsync(ct);
            return after is not null && after.Entries.Any(e => CharacterGid.CharacterIdOf(e.Uid) == characterId)
                ? BlockAddOutcome.AlreadyRegistered
                : BlockAddOutcome.ListFull;
        }
    }

    /// <summary>
    /// C-H3: whether <paramref name="ownerId"/> blocks <paramref name="characterId"/>. Used to withhold a
    /// 23512 add-friend notice from a player who has blocked the requester.
    /// This backs a defensive server-side policy only.
    /// </summary>
    public Task<bool> BlocksAsync(uint ownerId, uint characterId, CancellationToken ct = default)
    {
        ulong storedId = characterId;
        return _lists.Find(Builders<BlockListDocument>.Filter.And(
                Builders<BlockListDocument>.Filter.Eq(d => d.Id, ownerId),
                Builders<BlockListDocument>.Filter.ElemMatch(d => d.Entries, e => e.Uid == storedId)))
            .AnyAsync(ct);
    }

    /// <summary>
    /// Removes the character that <paramref name="uid"/> names from the owner's list (a no-op when it is
    /// not there). <paramref name="uid"/> is the 23007 value, i.e. whatever our 23506/23510 handed the
    /// client: the GID now, the bare character id from a list the client got before this change. Both
    /// carry the character id in their low dword, which is what every row stores.
    /// </summary>
    public Task RemoveAsync(uint ownerId, ulong uid, CancellationToken ct = default)
    {
        ulong characterId = CharacterGid.CharacterIdOf(uid);
        // Pull only when the entry is present, so a forged or repeated 23007 for an id the owner never
        // blocked matches nothing and writes nothing. The handler answers 0 either way.
        return _lists.UpdateOneAsync(
            Builders<BlockListDocument>.Filter.And(
                Builders<BlockListDocument>.Filter.Eq(d => d.Id, ownerId),
                Builders<BlockListDocument>.Filter.ElemMatch(d => d.Entries, e => e.Uid == characterId)),
            Builders<BlockListDocument>.Update.PullFilter(d => d.Entries, e => e.Uid == characterId),
            cancellationToken: ct);
    }
}

/// <summary>
/// Wire builders and parsers for the messenger block list: 23006/23510 add, 23007/23511 delete,
/// 23008/23506 list.
/// </summary>
public static class BlockListPackets
{
    /// <summary>
    /// Result code 0, success. It is the leading u16 of a 23506 list, of a successful 23510 and of a 23511 reply.
    /// </summary>
    public const ushort ResultOk = 0;

    /// <summary>Size of the u32 <c>innerLen</c> field that prefixes the record block of 23506/23510 and heads a 23006 body.</summary>
    private const int InnerLenFieldSize = sizeof(uint);

    /// <summary>Size of the u8 character-count field that precedes a UTF-16LE name.</summary>
    private const int NameCountFieldSize = sizeof(byte);

    /// <summary>Bytes of one UTF-16LE character of a name.</summary>
    private const int Utf16CharSize = sizeof(char);

    /// <summary>Offset of the first name's characters in a 23006 body: <c>[u32 innerLen][u8 n1]</c> precede them.</summary>
    private const int AddBlockNameOffset = InnerLenFieldSize + NameCountFieldSize;

    /// <summary>The u8 that closes every block-list record, after the name.</summary>
    private const byte RecordTerminator = 0;

    /// <summary>The uid of the empty record a refused 23510 carries (no character).</summary>
    private const ulong EmptyRecordUid = 0;

    /// <summary>
    /// Reads the name out of a 23006 ReqAddBlockList body:
    /// <c>[u32 innerLen][u8 n1][n1 x UTF-16LE][u8 n2][n2 x UTF-16LE]</c>. innerLen = 1 + 2*n1 + 1
    /// (e.g. 0x18 for the 11-character "iRO2ChatBot"). The second name is always empty (n2 = 0), so only
    /// the first is used.
    /// </summary>
    public static string? ReadAddBlockName(ReadOnlySpan<byte> body)
    {
        if (body.Length < AddBlockNameOffset) return null;
        var innerLen = BinaryPrimitives.ReadUInt32LittleEndian(body);
        int nChars = body[InnerLenFieldSize];
        if (nChars == 0
            || innerLen < NameCountFieldSize + Utf16CharSize * nChars
            || body.Length < AddBlockNameOffset + Utf16CharSize * nChars) return null;
        return Encoding.Unicode.GetString(body.Slice(AddBlockNameOffset, Utf16CharSize * nChars));
    }

    /// <summary>
    /// 23510 result for a name that is already on the owner's list (a second block with no 23007 in
    /// between). The client shows string 9514 for it, the same message as for 6, the already-a-friend
    /// code of 23508 (FriendStore.AddResultAlreadyFriend).
    /// </summary>
    public const ushort AddBlockResultAlreadyRegistered = 7;

    /// <summary>
    /// 23510 result for a typed name that resolves to no character. Every such 23006 is answered with
    /// this code and the empty record (<see cref="BuildAddBlockRefusal"/>), with a
    /// fixed header (result 18) followed by ten zero bytes. The client shows string_ui 9574 for case 18, "The
    /// character name you've entered does not exist." It is the same code the friend add sends for an
    /// unknown name (FriendStore.AddResultNotFound, 23508).
    /// </summary>
    public const ushort AddBlockResultNotFound = 18;

    /// <summary>
    /// Builds RMI 23506 AnsBlockList: <c>[u16 0][u32 innerLen][u16 count]{ record }</c>, record =
    /// <c>[u64 uid][u8 nChars][name UTF-16LE][u8 0]</c>. innerLen = length - 6, and each
    /// record ends with a 0 byte.
    /// The uid is the blocked character's GID (<see cref="BlockedCharacter.Gid"/>), above 2^32; the client
    /// looks chat senders up in its block map by that full value (<see cref="CharacterGid"/>).
    /// No entries gives the 8-byte <c>00 00 02 00 00 00 00 00</c> for an empty list.
    /// </summary>
    public static byte[] BuildBlockListBody(IReadOnlyList<BlockedCharacter> entries)
    {
        using var w = new PacketWriter();
        w.WriteUInt16(ResultOk);
        var lenPos = w.Reserve(InnerLenFieldSize);
        int start = w.Length;
        // Clamp the count so it cannot wrap the u16: BlockListRepository.MaxBlocked keeps it far below this,
        // but the wire field must never roll over if a document ever holds more.
        w.WriteUInt16((ushort)Math.Min(entries.Count, ushort.MaxValue));
        foreach (var e in entries)
            WriteRecord(w, e.Gid, e.Name);
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

    /// <summary>
    /// Builds RMI 23510 AnsAddBlock: <c>[u16 0][u32 innerLen][one 23506 record]</c>, the resolved
    /// character under its GID. A success is a leading result 0, then the inner length, the 64-bit GID,
    /// the name-length byte, the UTF-16 name, and a trailing 0.
    /// </summary>
    public static byte[] BuildAddBlockBody(ulong gid, string name)
    {
        using var w = new PacketWriter();
        w.WriteUInt16(ResultOk);
        var lenPos = w.Reserve(InnerLenFieldSize);
        int start = w.Length;
        WriteRecord(w, gid, name);
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

    /// <summary>
    /// Builds a refused RMI 23510: <c>[u16 result][u32 innerLen = 10][an empty record]</c>, i.e. uid 0,
    /// no name and the trailing 0. The re-block refusal (<see cref="AddBlockResultAlreadyRegistered"/>)
    /// is exactly <c>07 00 0a 00 00 00</c> followed by ten zero bytes, and the unknown-name refusal
    /// (<see cref="AddBlockResultNotFound"/>) the same bytes after <c>12 00</c>.
    /// </summary>
    public static byte[] BuildAddBlockRefusal(ushort result)
    {
        using var w = new PacketWriter();
        w.WriteUInt16(result);
        var lenPos = w.Reserve(InnerLenFieldSize);
        int start = w.Length;
        WriteRecord(w, EmptyRecordUid, string.Empty);
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

    /// <summary>
    /// Builds RMI 23511 AnsDelBlock: <c>[u16 0][u64 uid]</c>, the uid 23007 named echoed back.
    /// </summary>
    public static byte[] BuildDelBlockBody(ulong uid)
    {
        using var w = new PacketWriter();
        w.WriteUInt16(ResultOk);
        w.WriteUInt64(uid);
        return w.ToArray();
    }

    private static void WriteRecord(PacketWriter w, ulong uid, string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        w.WriteUInt64(uid);
        w.WriteByte((byte)(nameBytes.Length / Utf16CharSize));
        w.WriteBytes(nameBytes);
        w.WriteByte(RecordTerminator);
    }
}
