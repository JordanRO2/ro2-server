using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Packets;
using RO2.Characters.Domain;

namespace RO2.Server.Chat;

/// <summary>
/// Messenger friend lists, persisted in the <c>chat_friend_lists</c> collection (one document per owner
/// character), plus the S2C bodies of the add/delete-friend exchange (23508, 23509, 23512).
///
/// Ids: the stored list holds <c>Character.Id</c>s, and <see cref="Entry"/> puts that 32-bit id in the
/// record. What goes on the wire is the friend's GID (<see cref="CharacterGid"/>): the 23505 and 23508
/// senders swap it in (ReqFriendListHandler.WithGids, ReqAddFriendHandler), 23502/23503 name the friend by its
/// own <see cref="MessengerUid"/>, and 23005 comes back with it and is matched on its low dword. The client
/// matches the friend map against the block map by the whole u64, and the block list sends GIDs, so every
/// friend path has to as well.
///
/// Why persist at all: after every successful 23508 the client re-requests the lists at once
/// (23004, 23003). A friend that is not in the next 23505 would drop out of the window again straight away.
///
/// A scoped DI service: the storage methods are instance methods over the Mongo context and the scope's
/// <see cref="ICharacterRepository"/>; the result codes and wire builders are static.
/// </summary>
public sealed class FriendStore
{
    public const string CollectionName = "chat_friend_lists";

    /// <summary>Result codes of 23508 AnsAddFriend.</summary>
    public const ushort AddResultOk = 0;
    public const ushort AddResultAlreadyFriend = 6;
    public const ushort AddResultNotFound = 18;

    /// <summary>
    /// 23508 result 33: the REQUESTER's list is full. The client's messenger result table shows
    /// string_ui 2286 "You can't add anymore friends."; the client never checks the size itself (it refuses only
    /// an empty name, its own name and a name already in its map before sending 23002). The failure frame is
    /// <c>21 00 11 00 00 00</c> + 17 zero bytes, the same all-zero record the other failures carry.
    /// </summary>
    public const ushort AddResultListFull = 33;

    /// <summary>
    /// Friends one character may list. No data/ table holds a friend limit, so this is a server constant: 200.
    /// The limit is checked BEFORE the name is looked up, and it is measured on the count the owner's 23505 lists.
    /// </summary>
    public const int MaxFriends = 200;

    /// <summary>
    /// Profession (ProJob) level and id carried by a friend record's two trailer bytes. No profession is modelled,
    /// so both are 0, as in the character list and 2310.
    /// </summary>
    private const byte NoProJobLevel = 0;
    private const byte NoProJobId = 0;

    /// <summary>
    /// Length of the head of a one-entry <see cref="CommunityChannels.BuildFriendListBody"/> body that precedes the
    /// record: <c>[reserved:2][innerLen:4][count:2]</c>.
    /// </summary>
    private const int FriendListBodyHeaderLength = 2 + 4 + 2;

    /// <summary>Size of the 23508 head before the record: <c>[u16 result][u32 innerLen]</c>.</summary>
    private const int AddFriendAnswerHeaderLength = 2 + 4;

    /// <summary>Offset of the u32 record length in a 23508 body.</summary>
    private const int AddFriendAnswerLengthOffset = 2;

    /// <summary>23509 AnsDelFriend: the result is always 0 (a zero u16), and the uid follows at +2.</summary>
    private const int DelFriendAnswerUidOffset = 2;
    private const int DelFriendAnswerLength = 2 + 8;

    /// <summary>Width tag of a bare wide string on the wire: 2 = UTF-16LE, followed by a compact-length character count.</summary>
    private const byte WStrWidthTagUtf16 = 0x02;

    /// <summary>Bytes per UTF-16 code unit.</summary>
    private const int Utf16UnitSize = 2;

    /// <summary>The all-zero record that carries a failed 23508: uid 0, empty name and location, zero trailers.</summary>
    private static readonly CommunityChannels.FriendEntry FailureEntry =
        new(0, "", false, 0, 0, "", NoProJobLevel, NoProJobId);

    /// <summary>True when a list of <paramref name="count"/> friends (<see cref="CountAsync"/>) takes no more.</summary>
    public static bool IsListFull(int count) => count >= MaxFriends;

    /// <summary>One owner's friend list. <c>Id</c> is the owner's CharacterId.</summary>
    [BsonIgnoreExtraElements]
    public sealed class FriendListDocument
    {
        [BsonId]
        public uint Id { get; set; }
        public List<uint> FriendIds { get; set; } = new();
    }

    private readonly MongoDbContext _db;
    private readonly ICharacterRepository _characters;
    private readonly IChannelPresenceStore _presence;

    public FriendStore(MongoDbContext db, ICharacterRepository characters, IChannelPresenceStore presence)
    {
        _db = db;
        _characters = characters;
        _presence = presence;
    }

    private IMongoCollection<FriendListDocument> Lists()
        => _db.GetCollection<FriendListDocument>(CollectionName);

    /// <summary>
    /// Case-insensitive, exact-length name lookup. Names match without regard to case and the answer carries
    /// the stored spelling ("greeluck" finds "GreeLuck").
    /// Nothing else is normalised: a name with a trailing space is answered 18 (not found).
    /// </summary>
    public async Task<Character?> FindCharacterByNameAsync(string name)
    {
        return await _characters.FindByNameIgnoreCaseAsync(name);
    }

    /// <summary>Outcome of <see cref="AddAsync"/>: the three cases the 23508 result codes distinguish.</summary>
    public enum AddOutcome
    {
        /// <summary>The friend was added (23508 result 0).</summary>
        Added,
        /// <summary>The friend was already on the list, nothing written (23508 result 6).</summary>
        AlreadyFriend,
        /// <summary>The list was at the cap, nothing written (23508 result 33).</summary>
        ListFull,
    }

    /// <summary>
    /// Add <paramref name="friendId"/> to the owner's list.
    /// <para>
    /// The add is a single size-guarded <c>UpdateOne</c> — <c>$addToSet</c> runs only while the stored
    /// list holds fewer than <see cref="MaxFriends"/> ids ($expr/$size) — so two requests that both pass the
    /// handler's pre-lookup count check cannot both land and push the list past the cap. The guard counts the
    /// raw <see cref="FriendListDocument.FriendIds"/> array, which still holds ids of since-deleted characters
    /// that <see cref="CountAsync"/> leaves out; the handler keeps the live count for the 33 answer,
    /// and this is only the concurrency backstop. A full list that also already holds the friend answers
    /// <see cref="AddOutcome.AlreadyFriend"/>, matched by re-reading the document.
    /// </para>
    /// </summary>
    public async Task<AddOutcome> AddAsync(uint ownerId, uint friendId)
    {
        // The driver renders FriendIds.Count < MaxFriends as {"FriendIds.<Max-1>": {$exists: false}}, i.e. the
        // list has no element at the last index, so there is room for one more. $addToSet runs only then.
        var guard = Builders<FriendListDocument>.Filter.And(
            Builders<FriendListDocument>.Filter.Eq(d => d.Id, ownerId),
            Builders<FriendListDocument>.Filter.Where(d => d.FriendIds.Count < MaxFriends));
        var add = Builders<FriendListDocument>.Update.AddToSet(d => d.FriendIds, friendId);

        var result = await Lists().UpdateOneAsync(guard, add);
        if (result.ModifiedCount > 0) return AddOutcome.Added;
        if (result.MatchedCount > 0) return AddOutcome.AlreadyFriend; // had room, $addToSet was a no-op

        // No match: no list document yet, or the list is full. Re-read to tell those apart.
        var doc = await Lists().Find(d => d.Id == ownerId).FirstOrDefaultAsync();
        if (doc is not null)
            return doc.FriendIds.Contains(friendId) ? AddOutcome.AlreadyFriend : AddOutcome.ListFull;

        // Truly absent: create it. The guarded upsert keeps the cap even against a concurrent first-add, and
        // the loser of that create race gets a duplicate _id error, after which the document exists and one
        // conditional retry settles the outcome.
        try
        {
            await Lists().UpdateOneAsync(guard, add, new UpdateOptions { IsUpsert = true });
            return AddOutcome.Added;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var retry = await Lists().UpdateOneAsync(guard, add);
            if (retry.ModifiedCount > 0) return AddOutcome.Added;
            if (retry.MatchedCount > 0) return AddOutcome.AlreadyFriend;
            var after = await Lists().Find(d => d.Id == ownerId).FirstOrDefaultAsync();
            return after is not null && after.FriendIds.Contains(friendId) ? AddOutcome.AlreadyFriend : AddOutcome.ListFull;
        }
    }

    /// <summary>
    /// How many friends the owner's 23505 lists: the stored ids that are still a character that is not deleted,
    /// the same filter <see cref="GetEntriesAsync"/> applies, because the limit
    /// (<see cref="MaxFriends"/>) is measured against that 23505 count.
    /// </summary>
    public async Task<int> CountAsync(uint ownerId, CancellationToken ct)
    {
        var doc = await Lists().Find(d => d.Id == ownerId).FirstOrDefaultAsync(ct);
        if (doc == null || doc.FriendIds.Count == 0) return 0;
        return await _characters.CountLiveByIdsAsync(doc.FriendIds, ct);
    }

    /// <summary>
    /// Remove <paramref name="friendId"/> from the owner's list. The filter requires the id to be present, so
    /// a delete for an id the owner never listed (a forged or repeated 23005) matches nothing and writes
    /// nothing; the handler answers 0 either way.
    /// </summary>
    public async Task RemoveAsync(uint ownerId, uint friendId)
        => await Lists().UpdateOneAsync(
            d => d.Id == ownerId && d.FriendIds.Contains(friendId),
            Builders<FriendListDocument>.Update.Pull(d => d.FriendIds, friendId));

    /// <summary>Whether <paramref name="listOwnerId"/> has <paramref name="friendId"/> in its stored list.</summary>
    public async Task<bool> ListsFriendAsync(uint listOwnerId, uint friendId, CancellationToken ct)
    {
        var list = await Lists().Find(d => d.Id == listOwnerId).FirstOrDefaultAsync(ct);
        return list != null && list.FriendIds.Contains(friendId);
    }

    /// <summary>
    /// The owner's friends as 23505 records, in the order they were added. Deleted characters are skipped.
    /// For ReqFriendListHandler's 23505 body (<see cref="CommunityChannels.BuildFriendListBody"/>).
    /// The location of each online friend is the channel label the Zone recorded for it at its last world
    /// entry (<see cref="ChannelPresenceStore"/>), read in one query for all of them; offline friends are
    /// not looked up, as their record carries no location.
    /// </summary>
    public async Task<IReadOnlyList<CommunityChannels.FriendEntry>> GetEntriesAsync(
        ChatBroadcastService broadcast, uint ownerId, CancellationToken ct)
    {
        var doc = await Lists().Find(d => d.Id == ownerId).FirstOrDefaultAsync(ct);
        if (doc == null || doc.FriendIds.Count == 0)
            return Array.Empty<CommunityChannels.FriendEntry>();

        var byId = (await _characters.GetLiveByIdsAsync(doc.FriendIds, ct)).ToDictionary(c => c.Id);

        var online = byId.Keys.Where(id => IsOnline(broadcast, id)).ToHashSet();
        var locations = await _presence.GetLocationsAsync(online, ct);

        var entries = new List<CommunityChannels.FriendEntry>(doc.FriendIds.Count);
        foreach (var id in doc.FriendIds)
            if (byId.TryGetValue(id, out var c))
                entries.Add(Entry(c, online.Contains(id), locations.GetValueOrDefault(id)));
        return entries;
    }

    /// <summary>
    /// A character as a friend record, online when it has a logged-in messenger session here, with no
    /// location. The 23508 reply carries the channel label for an online friend, so
    /// ReqAddFriendHandler builds its record with <see cref="Entry"/> and <see cref="ChannelPresenceStore"/>
    /// instead; this location-less form has no caller on the live path.
    /// </summary>
    public static CommunityChannels.FriendEntry ToEntry(Character c, ChatBroadcastService broadcast)
        => Entry(c, IsOnline(broadcast, c.Id), location: null);

    /// <summary>
    /// A character as a 23505/23508 friend record, given whether it is online and its channel label.
    /// Location: filled with the friend's current channel name ('Channel 1') exactly when online, so an offline
    /// record never carries one. Our channels are all 'Channel N'; there are no dungeon-named servers here.
    /// TrailerA/TrailerB are the ProJob level and ProJob id, and the job byte is the combat class. No profession
    /// is modelled: CharacterSerializer.WireProJob sends (0, 0) in the char list and 2310, so the friend record
    /// says the same. Filling them needs a persisted ProJob level/id on Character (the Chat server cannot call
    /// the Zone's WireProJob), which does not exist yet.
    /// </summary>
    public static CommunityChannels.FriendEntry Entry(Character c, bool online, string? location)
        => new((ulong)c.Id, c.Name, online, c.Level, c.Job,
               Location: online ? location ?? "" : "", TrailerA: NoProJobLevel, TrailerB: NoProJobId);

    public static bool IsOnline(ChatBroadcastService broadcast, uint characterId)
        => broadcast.Sessions.Any(s => s.IsConnected && s.CharacterId == characterId);

    /// <summary>
    /// 23508 AnsAddFriend: [u16 result][u32 innerLen][one friend record], innerLen == len-6.
    /// A failure carries an all-zero 17-byte record (uid 0, empty name, zeros, empty location, zero
    /// trailers): 12 00 / 06 00 / 21 00 followed by 11 00 00 00 and 17 zero bytes.
    /// The record comes from <see cref="CommunityChannels.BuildFriendListBody"/>, whose one-entry body is
    /// [reserved:2][innerLen:4][count:2][record], so the record is everything from byte 8.
    /// </summary>
    public static byte[] BuildAddFriendAnswer(ushort result, CommunityChannels.FriendEntry? entry)
    {
        var e = entry ?? FailureEntry;
        var record = CommunityChannels.BuildFriendListBody(new[] { e }).AsSpan(FriendListBodyHeaderLength);
        var body = new byte[AddFriendAnswerHeaderLength + record.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), result);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(AddFriendAnswerLengthOffset), (uint)record.Length);
        record.CopyTo(body.AsSpan(AddFriendAnswerHeaderLength));
        return body;
    }

    /// <summary>
    /// 23509 AnsDelFriend: [u16 result][u64 uid], the request's uid echoed back with result 0.
    /// </summary>
    public static byte[] BuildDelFriendAnswer(ulong uid)
    {
        var body = new byte[DelFriendAnswerLength];
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(DelFriendAnswerUidOffset), uid);
        return body;
    }

    /// <summary>
    /// 23512 NfyAddFriend, sent to the player who was just added: a bare WStr of the requester's name,
    /// [widthTag:u8=02][CompactLen nChars][UTF-16LE]. It carries the REQUESTER's name, not the receiver's.
    /// </summary>
    public static byte[] BuildNfyAddFriend(string requesterName)
    {
        var name = Encoding.Unicode.GetBytes(requesterName);
        using var w = new PacketWriter(4 + name.Length);
        w.WriteByte(WStrWidthTagUtf16);
        w.WriteCompactLength(name.Length / Utf16UnitSize);
        w.WriteBytes(name);
        return w.ToArray();
    }
}
