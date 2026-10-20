using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Mongo.Persistence;

/// <summary>
/// The channel between the Zone and Chat servers for close-range ("local") chat rooms: a capped MongoDB
/// collection both append to. The Zone tails it for joins and exits; the Chat server looks up the Zone's
/// open signal of a room when a player asks to join it.
/// </summary>
/// <remarks>
/// <para>
/// WHY A CHANNEL. A close-range room lives on two servers at once. The Zone opens it (1533 -> 2762) and
/// shows its sign to the area (2763), but players join and leave it on the Chat server (23020 -> 23538,
/// 23022 -> 23541), and retail then updates the sign from the Zone: 2765 when the owner leaves (right after
/// the Chat's 23541, as the Chat and Zone connections are separate), and 2764 + 2770 when someone joins (right after
/// the joiner's 23538 on the Chat). The two processes here share nothing else: the only event bus,
/// InMemoryEventBus, is in-process and only Login registers it.
/// </para>
/// <para>
/// DIRECTIONS. Zone to Chat: <see cref="CloseRangeRoomSignals.OpenKind"/>, written by the 1533 handler, is what the
/// Chat server learns a room's owner, capacity and password from (it never sees the 1533), read on demand by
/// <see cref="FindOpenRoomAsync"/>. Chat to Zone: <see cref="CloseRangeRoomSignals.JoinKind"/> and
/// <see cref="CloseRangeRoomSignals.ExitKind"/>, tailed by the Zone (<see cref="SubscribeAsync"/>).
/// </para>
/// <para>
/// WHY THIS CHANNEL. Both processes already hold a <see cref="MongoDbContext"/>. A tailable cursor on a capped
/// collection works on a standalone mongod (change streams need a replica set) and needs no new port,
/// package or configuration. With <see cref="CursorType.TailableAwait"/> the server holds each getMore open
/// until a document arrives, so a signal reaches the Zone in milliseconds, which is the gap retail shows.
/// The collection is capped (<see cref="CappedBytes"/>, <see cref="CappedDocuments"/>), so it never grows;
/// an open signal pushed out by <see cref="CappedDocuments"/> newer ones makes its room unjoinable (the
/// joiner is told the room cannot be found), which takes thousands of rooms opened or left in between.
/// </para>
/// </remarks>
public sealed class CloseRangeRoomSignalBus : ICloseRangeRoomSignals
{
 /// <summary>The capped collection, in the servers' shared database.</summary>
    public const string CollectionName = "close_range_room_signals";

 /// <summary>Capped size in bytes. A signal is well under 200 bytes, so <see cref="CappedDocuments"/> of
 /// them fit.</summary>
    public const long CappedBytes = 1L << 20;

 /// <summary>Capped document count: far more than can be in flight between two servers on one host, and
 /// room for an open signal to outlive thousands of later opens, joins and exits.</summary>
    public const long CappedDocuments = 4096;

 /// <summary>MongoDB's NamespaceExists error: another process created the collection first.</summary>
    private const int NamespaceExistsCode = 48;

 /// <summary>A <see cref="SignalDocument.NewMasterCharacterId"/> of 0 means the exit handed the room over to no one.</summary>
    private const uint NoNewMasterCharacterId = 0;

 /// <summary>How long the tailable cursor waits server-side for a new signal before returning an empty batch.</summary>
    private static readonly TimeSpan TailAwaitTime = TimeSpan.FromSeconds(1);

 /// <summary>How long the tail waits before re-opening a cursor that died or failed.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

 /// <summary>
 /// <see cref="SignalDocument.Kind"/> of the marker the tail writes before it opens a cursor. A tailable
 /// cursor whose first batch matches nothing is closed by the server at once, so on an empty or quiet
 /// collection the tail would only see a signal at its next re-open, up to <see cref="RetryDelay"/> late.
 /// The marker is newer than anything the tail has seen, so the cursor opens on it and stays open.
 /// Markers are never handed to the caller.
 /// </summary>
    public const string TailMarkerKind = "tail-open";

    private static volatile bool _ensured;

    private readonly MongoDbContext _context;

    public CloseRangeRoomSignalBus(MongoDbContext context) => _context = context;

 /// <summary>The stored shape of one signal (the former CloseRangeRoomSignal): the property names are the
 /// BSON element names, so documents are unchanged. Private; callers see <see cref="CloseRangeRoomSignal"/>.</summary>
    private sealed class SignalDocument
    {
 /// <summary>Insertion order within the capped collection; the tail resumes after the last one seen.</summary>
        [BsonId] public ObjectId Id { get; set; }
        public string Kind { get; set; } = CloseRangeRoomSignals.ExitKind;
        public byte[] RoomGuid { get; set; } = [];
        public uint CharacterId { get; set; }
        public byte Capacity { get; set; }
        public byte[] Password { get; set; } = [];
        public int MemberCount { get; set; }
        public uint NewMasterCharacterId { get; set; }
        public bool OwnerGone { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    private static CloseRangeRoomSignal ToSignal(SignalDocument d) => new()
    {
        Id = d.Id.ToString(),
        Kind = d.Kind,
        RoomGuid = d.RoomGuid,
        CharacterId = d.CharacterId,
        Capacity = d.Capacity,
        Password = d.Password,
        MemberCount = d.MemberCount,
        NewMasterCharacterId = d.NewMasterCharacterId,
        OwnerGone = d.OwnerGone,
        CreatedUtc = d.CreatedUtc,
    };

    private IMongoCollection<SignalDocument> Collection()
        => _context.GetCollection<SignalDocument>(CollectionName);

 /// <summary>
 /// Makes sure the collection exists and is capped, which a tailable cursor requires: creates it capped,
 /// or converts it when something created it plain (an insert into a missing collection creates a plain
 /// one). Once per process; safe to call from both servers at once.
 /// </summary>
    private async Task EnsureCappedAsync(CancellationToken ct)
    {
        if (_ensured) return;
        var db = Collection().Database;

        var existing = await (await db.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", CollectionName) }, ct)).FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            try
            {
                await db.CreateCollectionAsync(CollectionName,
                    new CreateCollectionOptions { Capped = true, MaxSize = CappedBytes, MaxDocuments = CappedDocuments }, ct);
            }
            catch (MongoCommandException ex) when (ex.Code == NamespaceExistsCode)
            {
                
                existing = await (await db.ListCollectionsAsync(
                    new ListCollectionsOptions { Filter = new BsonDocument("name", CollectionName) }, ct)).FirstOrDefaultAsync(ct);
            }
        }

        if (existing is not null && !IsCapped(existing))
        {
            await db.RunCommandAsync<BsonDocument>(
                new BsonDocument { { "convertToCapped", CollectionName }, { "size", CappedBytes } }, cancellationToken: ct);
        }
        _ensured = true;
    }

    private static bool IsCapped(BsonDocument collectionInfo)
        => collectionInfo.TryGetValue("options", out var options) && options.IsBsonDocument
            && options.AsBsonDocument.TryGetValue("capped", out var capped) && capped.ToBoolean();

    public Task PublishOpenAsync(ReadOnlyMemory<byte> roomGuid, uint ownerCharacterId, byte capacity,
        ReadOnlyMemory<byte> password, CancellationToken ct = default)
        => InsertAsync(new SignalDocument
        {
            Kind = CloseRangeRoomSignals.OpenKind,
            RoomGuid = roomGuid.ToArray(),
            CharacterId = ownerCharacterId,
            Capacity = capacity,
            Password = password.ToArray(),
        }, ct);

    public Task PublishJoinAsync(ReadOnlyMemory<byte> roomGuid, uint characterId, int memberCount,
        CancellationToken ct = default)
        => InsertAsync(new SignalDocument
        {
            Kind = CloseRangeRoomSignals.JoinKind,
            RoomGuid = roomGuid.ToArray(),
            CharacterId = characterId,
            MemberCount = memberCount,
        }, ct);

    public Task PublishExitAsync(ReadOnlyMemory<byte> roomGuid, uint characterId, int memberCount,
        uint newMasterCharacterId, bool ownerGone, CancellationToken ct = default)
        => InsertAsync(new SignalDocument
        {
            Kind = CloseRangeRoomSignals.ExitKind,
            RoomGuid = roomGuid.ToArray(),
            CharacterId = characterId,
            MemberCount = memberCount,
            NewMasterCharacterId = newMasterCharacterId,
            OwnerGone = ownerGone,
        }, ct);

    private async Task InsertAsync(SignalDocument signal, CancellationToken ct)
    {
        await EnsureCappedAsync(ct);
        signal.Id = ObjectId.GenerateNewId();
        signal.CreatedUtc = DateTime.UtcNow;
        await Collection().InsertOneAsync(signal, cancellationToken: ct);
    }

    public async Task<CloseRangeRoomSignal?> FindOpenRoomAsync(ReadOnlyMemory<byte> roomGuid,
        CancellationToken ct = default)
    {
        var guid = roomGuid.ToArray();
        var f = Builders<SignalDocument>.Filter;
        var collection = Collection();
        var open = await collection.Find(f.And(f.Eq(s => s.Kind, CloseRangeRoomSignals.OpenKind), f.Eq(s => s.RoomGuid, guid)))
            .SortByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        if (open is null) return null;

        
        
        var exits = await collection.Find(f.And(f.Gt(s => s.Id, open.Id), f.Eq(s => s.Kind, CloseRangeRoomSignals.ExitKind), f.Eq(s => s.RoomGuid, guid)))
            .SortBy(s => s.Id).ToListAsync(ct);
        var owners = new HashSet<uint> { open.CharacterId };
        foreach (var e in exits)
            if (e.NewMasterCharacterId != NoNewMasterCharacterId) owners.Add(e.NewMasterCharacterId);
        var opens = await collection.Find(f.And(f.Gt(s => s.Id, open.Id), f.Eq(s => s.Kind, CloseRangeRoomSignals.OpenKind), f.In(s => s.CharacterId, owners)))
            .SortBy(s => s.Id).ToListAsync(ct);

        if (CloseRangeRoomSignals.CurrentOwner(ToSignal(open),
                exits.Concat(opens).OrderBy(s => s.Id).Select(ToSignal)) is not uint owner)
            return null;
        return new CloseRangeRoomSignal
        {
            Id = open.Id.ToString(),
            Kind = open.Kind,
            RoomGuid = open.RoomGuid,
            CharacterId = owner,
            Capacity = open.Capacity,
            Password = open.Password,
            CreatedUtc = open.CreatedUtc,
        };
    }

    public async Task<CloseRangeRoomHistory?> GetRoomHistoryAsync(ReadOnlyMemory<byte> roomGuid,
        CancellationToken ct = default)
    {
        var guid = roomGuid.ToArray();
        var f = Builders<SignalDocument>.Filter;
        var collection = Collection();
        var open = await collection.Find(f.And(f.Eq(s => s.Kind, CloseRangeRoomSignals.OpenKind), f.Eq(s => s.RoomGuid, guid)))
            .SortByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        if (open is null) return null;

        var ofRoom = await collection.Find(f.And(f.Gt(s => s.Id, open.Id), f.Eq(s => s.RoomGuid, guid),
                f.In(s => s.Kind, new[] { CloseRangeRoomSignals.JoinKind, CloseRangeRoomSignals.ExitKind })))
            .SortBy(s => s.Id).ToListAsync(ct);
        var owners = new HashSet<uint> { open.CharacterId };
        foreach (var e in ofRoom)
            if (e.Kind == CloseRangeRoomSignals.ExitKind && e.NewMasterCharacterId != NoNewMasterCharacterId) owners.Add(e.NewMasterCharacterId);
        var opens = await collection.Find(f.And(f.Gt(s => s.Id, open.Id), f.Eq(s => s.Kind, CloseRangeRoomSignals.OpenKind),
                f.In(s => s.CharacterId, owners)))
            .SortBy(s => s.Id).ToListAsync(ct);

        return new CloseRangeRoomHistory(ToSignal(open),
            ofRoom.Concat(opens).OrderBy(s => s.Id).Select(ToSignal).ToList());
    }

    public async Task SubscribeAsync(DateTime fromUtc, Func<CloseRangeRoomSignal, Task> onSignal,
        Action<Exception> onError, CancellationToken ct)
    {
        var collection = Collection();
        ObjectId? last = null;
        var options = new FindOptions<SignalDocument>
        {
            CursorType = CursorType.TailableAwait,
            NoCursorTimeout = true,
            MaxAwaitTime = TailAwaitTime,
        };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureCappedAsync(ct);
                await collection.InsertOneAsync(new SignalDocument
                {
                    Id = ObjectId.GenerateNewId(), Kind = TailMarkerKind, CreatedUtc = DateTime.UtcNow,
                }, cancellationToken: ct);
                var filter = last is { } id
                    ? Builders<SignalDocument>.Filter.Gt(s => s.Id, id)
                    : Builders<SignalDocument>.Filter.Gte(s => s.CreatedUtc, fromUtc);
                using var cursor = await collection.FindAsync(filter, options, ct);
                while (await cursor.MoveNextAsync(ct))
                {
                    foreach (var signal in cursor.Current)
                    {
                        last = signal.Id;
                        if (signal.Kind != TailMarkerKind)
                            await onSignal(ToSignal(signal));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                onError(ex);
            }

            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
