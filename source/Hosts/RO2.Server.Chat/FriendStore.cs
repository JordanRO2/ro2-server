using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Repositories;
using RO2.Characters.Domain;
using RO2.Socials.Domain;
using RO2.Socials.Infrastructure.Mongo;

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
/// A scoped DI service: the raw list persistence is delegated to <see cref="IFriendListRepository"/>; this
/// facade adds what needs the scope's <see cref="ICharacterRepository"/> and presence store. The result codes
/// are static; the wire builders live in <see cref="SocialPackets"/>.
/// </summary>
public sealed class FriendStore
{
    public const string CollectionName = MongoFriendListRepository.CollectionName;

    /// <summary>Result codes of 23508 AnsAddFriend.</summary>
    public const ushort AddResultOk = SocialRules.ResultOk;
    public const ushort AddResultAlreadyFriend = SocialRules.AddResultAlreadyFriend;
    public const ushort AddResultNotFound = SocialRules.AddResultNotFound;

    /// <summary>
    /// 23508 result 33: the REQUESTER's list is full. The client's messenger result table shows
    /// string_ui 2286 "You can't add anymore friends."; the client never checks the size itself (it refuses only
    /// an empty name, its own name and a name already in its map before sending 23002). The failure frame is
    /// <c>21 00 11 00 00 00</c> + 17 zero bytes, the same all-zero record the other failures carry.
    /// </summary>
    public const ushort AddResultListFull = SocialRules.AddResultListFull;

    /// <summary>
    /// Friends one character may list. No data/ table holds a friend limit, so this is a server constant: 200.
    /// The limit is checked BEFORE the name is looked up, and it is measured on the count the owner's 23505 lists.
    /// </summary>
    public const int MaxFriends = SocialRules.MaxFriends;

    /// <summary>
    /// Profession (ProJob) level and id carried by a friend record's two trailer bytes. No profession is modelled,
    /// so both are 0, as in the character list and 2310.
    /// </summary>
    private const byte NoProJobLevel = 0;
    private const byte NoProJobId = 0;

    /// <summary>True when a list of <paramref name="count"/> friends (<see cref="CountAsync"/>) takes no more.</summary>
    public static bool IsListFull(int count) => SocialRules.IsFriendListFull(count);

    private readonly IFriendListRepository _lists;
    private readonly ICharacterRepository _characters;
    private readonly IChannelPresenceStore _presence;

    public FriendStore(IFriendListRepository lists, ICharacterRepository characters, IChannelPresenceStore presence)
    {
        _lists = lists;
        _characters = characters;
        _presence = presence;
    }

    /// <summary>
    /// Case-insensitive, exact-length name lookup. Names match without regard to case and the answer carries
    /// the stored spelling ("greeluck" finds "GreeLuck").
    /// Nothing else is normalised: a name with a trailing space is answered 18 (not found).
    /// </summary>
    public async Task<Character?> FindCharacterByNameAsync(string name)
    {
        return await _characters.FindByNameIgnoreCaseAsync(name);
    }

    /// <summary>
    /// Add <paramref name="friendId"/> to the owner's list. The size guard (<see cref="MaxFriends"/>) counts the
    /// raw stored array, which still holds ids of since-deleted characters that <see cref="CountAsync"/> leaves
    /// out; the handler keeps the live count for the 33 answer, and the guard is only the concurrency backstop.
    /// </summary>
    public Task<AddOutcome> AddAsync(uint ownerId, uint friendId) => _lists.AddAsync(ownerId, friendId);

    /// <summary>
    /// How many friends the owner's 23505 lists: the stored ids that are still a character that is not deleted,
    /// the same filter <see cref="GetEntriesAsync"/> applies, because the limit
    /// (<see cref="MaxFriends"/>) is measured against that 23505 count.
    /// </summary>
    public async Task<int> CountAsync(uint ownerId, CancellationToken ct)
    {
        var ids = await _lists.LoadAsync(ownerId, ct);
        if (ids.Count == 0) return 0;
        return await _characters.CountLiveByIdsAsync(ids, ct);
    }

    /// <summary>
    /// Remove <paramref name="friendId"/> from the owner's list. A delete for an id the owner never listed (a
    /// forged or repeated 23005) writes nothing; the handler answers 0 either way.
    /// </summary>
    public Task RemoveAsync(uint ownerId, uint friendId) => _lists.RemoveAsync(ownerId, friendId);

    /// <summary>Whether <paramref name="listOwnerId"/> has <paramref name="friendId"/> in its stored list.</summary>
    public Task<bool> ListsFriendAsync(uint listOwnerId, uint friendId, CancellationToken ct)
        => _lists.ListsFriendAsync(listOwnerId, friendId, ct);

    /// <summary>
    /// The owner's friends as 23505 records, in the order they were added. Deleted characters are skipped.
    /// For ReqFriendListHandler's 23505 body (<see cref="SocialPackets.BuildFriendListBody"/>).
    /// The location of each online friend is the channel label the Zone recorded for it at its last world
    /// entry (<see cref="ChannelPresenceStore"/>), read in one query for all of them; offline friends are
    /// not looked up, as their record carries no location.
    /// </summary>
    public async Task<IReadOnlyList<SocialPackets.FriendEntry>> GetEntriesAsync(
        ChatBroadcastService broadcast, uint ownerId, CancellationToken ct)
    {
        var friendIds = await _lists.LoadAsync(ownerId, ct);
        if (friendIds.Count == 0)
            return Array.Empty<SocialPackets.FriendEntry>();

        var byId = (await _characters.GetLiveByIdsAsync(friendIds, ct)).ToDictionary(c => c.Id);

        var online = byId.Keys.Where(id => IsOnline(broadcast, id)).ToHashSet();
        var locations = await _presence.GetLocationsAsync(online, ct);

        var entries = new List<SocialPackets.FriendEntry>(friendIds.Count);
        foreach (var id in friendIds)
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
    public static SocialPackets.FriendEntry ToEntry(Character c, ChatBroadcastService broadcast)
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
    public static SocialPackets.FriendEntry Entry(Character c, bool online, string? location)
        => new((ulong)c.Id, c.Name, online, c.Level, c.Job,
               Location: online ? location ?? "" : "", TrailerA: NoProJobLevel, TrailerB: NoProJobId);

    public static bool IsOnline(ChatBroadcastService broadcast, uint characterId)
        => broadcast.Sessions.Any(s => s.IsConnected && s.CharacterId == characterId);
}
