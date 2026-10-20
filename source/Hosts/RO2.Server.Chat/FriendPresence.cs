using RO2.Application.Interfaces;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat;

/// <summary>
/// Friend online/offline notices: 23502 NfyLoginPlayer_RegisterFriend and 23503
/// NfyLogoutPlayer_RegisterFriend. They go to every connected player who has the character in their
/// friend list.
///
/// Receivers. The named uid is always in the receiver's latest 23505 friend list, and is never the receiver's
/// own login uid. The list that is checked is the RECEIVER's list, which is the direction
/// <see cref="FriendStore"/> stores: one document per owner, holding the ids of the characters that owner
/// added. So the owners to notify are the documents whose FriendIds contain the character. The uid is the
/// character's own GID (its <see cref="MessengerUid"/>), the value the 23505/23508 records carry and the client
/// keys the friend by (ReqFriendListHandler.WithGids). It is a 64-bit value above 2^32, and it equals the uid
/// the friend's own 23001 login carried.
///
/// The notices only ever flip a friend's state: a 23502 names a friend last seen offline and a 23503 one last
/// seen online. So both notices here fire only on the character's first connection and its last, not on a
/// second connection that overlaps a relog.
///
/// Triggers:
/// - 23502 is sent on the friend's messenger login (23001), right after its 23501/23534 answers.
/// - 23503 is sent when the friend's messenger connection closes: on client exit, and on a character change
///   (1418 ReqChangeCharacterInGame), because the next 23001 arrives on a NEW messenger connection. A channel
///   change goes through a messenger reconnect too, so the same uid comes back as a 23503 followed by a 23502.
/// </summary>
public sealed class FriendPresence
{
    private readonly MongoDbContext _db;
    private readonly IChannelPresenceStore _presence;

    public FriendPresence(MongoDbContext db, IChannelPresenceStore presence)
    {
        _db = db;
        _presence = presence;
    }

    /// <summary>
    /// 23502 body (U32 family): [u32 innerLen = len-4][u64 uid][u8 nChars][location UTF-16LE].
    /// The location is never empty for a logged-in friend: it is the channel label ('Channel N', or a dungeon
    /// label such as 'W1Dungeon_N'). The Zone records the channel a character picked in ChannelPresenceStore at its
    /// 1417, which comes before the 1516/23001 messenger login, so the notice reads it from there; a character
    /// with no stored label gets "".
    /// </summary>
    public static byte[] BuildLoginNotice(ulong uid, string location)
    {
        var loc = Encoding.Unicode.GetBytes(location);
        int chars = Math.Min(loc.Length / 2, byte.MaxValue);
        var body = new byte[4 + 8 + 1 + chars * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), (uint)(body.Length - 4));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), uid);
        body[12] = (byte)chars;
        loc.AsSpan(0, chars * 2).CopyTo(body.AsSpan(13));
        return body;
    }

    /// <summary>23503 body: the bare u64 uid (8 bytes).</summary>
    public static byte[] BuildLogoutNotice(ulong uid)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(body, uid);
        return body;
    }

    /// <summary>
    /// 23502 to the online owners of lists that contain the character that has just logged in on
    /// <paramref name="session"/>. Call it after the 23501/23534 answers.
    /// The notice is not sent while another connection of the same character is still logged in. The
    /// owners already see that character online, both from that connection's own 23502 and from
    /// <see cref="FriendStore.IsOnline"/> in their 23505. A 23502 is never sent for a friend the
    /// receiver already sees online (see the class summary). This mirrors the rule in
    /// <see cref="NotifyLogoutAsync"/>.
    /// </summary>
    public async Task NotifyLoginAsync(ChatBroadcastService broadcast, ProudSession session)
    {
        if (session.CharacterId is not uint charId || !MessengerUid.TryGet(session, out var gid)) return;
        if (HasOtherLiveConnection(broadcast, charId, session)) return;
        var location = (await _presence.GetLocationsAsync(new[] { charId }, CancellationToken.None))
            .GetValueOrDefault(charId) ?? "";
        var body = BuildLoginNotice(gid, location);
        await SendToOwnersAsync(broadcast, charId, session, ChatS2COpcodes.NfyLoginPlayer_RegisterFriend, body);
    }

    /// <summary>
    /// 23503 to the online owners of lists that contain the character of the closing
    /// <paramref name="closing"/> session. Call it from ProudServer.SessionDisconnected. That event fires
    /// after the session has left the server's session table but before it is disposed. So
    /// <c>closing.CharacterId</c> is still readable, and <see cref="ChatBroadcastService.Sessions"/> no
    /// longer includes the closing session.
    /// The notice is not sent while another connection of the same character is still logged in. A relog
    /// can overlap the old connection, and announcing the character offline then would show a live friend
    /// as gone. The next close, of the last connection, sends it.
    /// </summary>
    public async Task NotifyLogoutAsync(ChatBroadcastService broadcast, ProudSession closing)
    {
        if (closing.CharacterId is not uint charId || !MessengerUid.TryGet(closing, out var gid)) return;
        if (HasOtherLiveConnection(broadcast, charId, closing)) return;
        var body = BuildLogoutNotice(gid);
        await SendToOwnersAsync(broadcast, charId, closing, ChatS2COpcodes.NfyLogoutPlayer_RegisterFriend, body);
    }

    /// <summary>
    /// Sends <paramref name="opcode"/> to every online owner of a list that contains <paramref name="charId"/>,
    /// except owners who have blocked the character.
    /// Defensive server policy: a presence notice is withheld from an owner whose block list holds the character.
    /// </summary>
    private async Task SendToOwnersAsync(
        ChatBroadcastService broadcast, uint charId, ProudSession subject, ushort opcode, byte[] body)
    {
        var owners = await OnlineOwnersAsync(broadcast, charId, subject);
        if (owners.Count == 0) return;

        var blockers = await OwnersBlockingAsync(owners.Select(o => o.CharacterId!.Value), charId);
        foreach (var owner in owners)
            if (!blockers.Contains(owner.CharacterId!.Value))
                await TrySendAsync(owner, opcode, body);
    }

    /// <summary>The subset of <paramref name="ownerIds"/> whose block list holds <paramref name="charId"/>.</summary>
    private async Task<HashSet<uint>> OwnersBlockingAsync(IEnumerable<uint> ownerIds, uint charId)
    {
        var ids = ownerIds.ToList();
        if (ids.Count == 0) return new HashSet<uint>();

        ulong storedId = charId;
        var filter = Builders<BlockListDocument>.Filter.And(
            Builders<BlockListDocument>.Filter.In(d => d.Id, ids),
            Builders<BlockListDocument>.Filter.ElemMatch(d => d.Entries, e => e.Uid == storedId));
        var blockers = await _db.GetCollection<BlockListDocument>(BlockListRepository.CollectionName)
            .Find(filter).Project(d => d.Id).ToListAsync();
        return blockers.ToHashSet();
    }

    /// <summary>
    /// Connected sessions whose character has <paramref name="charId"/> in its friend list. The character's
    /// own sessions are left out, because a notice never names the receiver itself.
    /// </summary>
    private async Task<List<ProudSession>> OnlineOwnersAsync(
        ChatBroadcastService broadcast, uint charId, ProudSession subject)
    {
        var ownerIds = await _db.GetCollection<FriendStore.FriendListDocument>(FriendStore.CollectionName)
            .Find(Builders<FriendStore.FriendListDocument>.Filter.AnyEq(d => d.FriendIds, charId))
            .Project(d => d.Id)
            .ToListAsync();
        if (ownerIds.Count == 0) return new List<ProudSession>();

        var owners = ownerIds.ToHashSet();
        return broadcast.Sessions
            .Where(s => !ReferenceEquals(s, subject) && s.IsConnected
                        && s.CharacterId is uint id && id != charId && owners.Contains(id))
            .ToList();
    }

    /// <summary>Whether a logged-in connection other than <paramref name="self"/> carries the character.</summary>
    private static bool HasOtherLiveConnection(ChatBroadcastService broadcast, uint charId, ProudSession self)
        => broadcast.Sessions.Any(s => !ReferenceEquals(s, self) && s.IsConnected && s.CharacterId == charId);

    private static async Task TrySendAsync(ProudSession target, ushort opcode, byte[] body)
    {
        try
        {
            await target.SendInternalRmiAsync(opcode, body);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            // The receiver's connection went away between the lookup and the send. It is disposed, or its
            // socket failed but its own disconnect has not been processed yet. There is nothing to tell it
            // now. Catching here keeps one dead receiver from stopping the notice to the others.
        }
    }
}
