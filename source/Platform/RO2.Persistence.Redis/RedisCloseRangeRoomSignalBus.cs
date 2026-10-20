using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using StackExchange.Redis;

namespace RO2.Persistence.Redis;

/// <summary>
/// Close-range room signal bus backed by a Redis Stream. Implements <see cref="ICloseRangeRoomSignals"/>.
/// </summary>
/// <remarks>
/// Each signal is one stream entry holding its kind, room, character, capacity, password, member count,
/// new master, owner-gone flag and creation ticks. The stream is trimmed to approximately
/// <see cref="CappedDocuments"/> entries. The entry id (ms-seq) is the insertion-order key.
/// Signal ids are zero-padded so that ordinal comparison matches insertion order.
/// Subscriptions poll the stream, since StackExchange.Redis has no blocking XREAD.
/// </remarks>
public sealed class RedisCloseRangeRoomSignalBus : ICloseRangeRoomSignals
{
 /// <summary>The Redis stream key that holds the signals.</summary>
    public const string StreamKey = "close_range_room_signals";

 /// <summary>Approximate entry cap for the stream.</summary>
    public const int CappedDocuments = 4096;

 /// <summary>Delay before retrying a subscription cycle after a failure.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

 /// <summary>Delay between polls when a subscription has no new entries.</summary>
    public static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(250);

    private const int BatchSize = 256;

    private readonly RedisConnection _redis;

    public RedisCloseRangeRoomSignalBus(RedisConnection redis) => _redis = redis;

    public Task PublishOpenAsync(ReadOnlyMemory<byte> roomGuid, uint ownerCharacterId, byte capacity,
        ReadOnlyMemory<byte> password, CancellationToken ct = default)
        => AddAsync(CloseRangeRoomSignals.OpenKind, roomGuid, ownerCharacterId, capacity, password, 0, 0, false, ct);

    public Task PublishJoinAsync(ReadOnlyMemory<byte> roomGuid, uint characterId, int memberCount,
        CancellationToken ct = default)
        => AddAsync(CloseRangeRoomSignals.JoinKind, roomGuid, characterId, 0, default, memberCount, 0, false, ct);

    public Task PublishExitAsync(ReadOnlyMemory<byte> roomGuid, uint characterId, int memberCount,
        uint newMasterCharacterId, bool ownerGone, CancellationToken ct = default)
        => AddAsync(CloseRangeRoomSignals.ExitKind, roomGuid, characterId, 0, default, memberCount,
            newMasterCharacterId, ownerGone, ct);

    private async Task AddAsync(string kind, ReadOnlyMemory<byte> roomGuid, uint characterId, byte capacity,
        ReadOnlyMemory<byte> password, int memberCount, uint newMasterCharacterId, bool ownerGone, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NameValueEntry[] fields =
        [
            new("kind", kind),
            new("room", Convert.ToBase64String(roomGuid.Span)),
            new("cid", characterId),
            new("cap", (int)capacity),
            new("pw", Convert.ToBase64String(password.Span)),
            new("mc", memberCount),
            new("nm", newMasterCharacterId),
            new("og", ownerGone ? "1" : "0"),
            new("ts", DateTime.UtcNow.Ticks),
        ];
        await _redis.Db.StreamAddAsync(StreamKey, fields, maxLength: CappedDocuments, useApproximateMaxLength: true);
    }

 /// <summary>Zero-pads a raw entry id ("ms-seq") so that ordinal order equals insertion order.</summary>
    private static string NormalizeId(RedisValue rawId)
    {
        var s = rawId.ToString();
        var dash = s.IndexOf('-');
        return dash < 0 ? s.PadLeft(20, '0') : s[..dash].PadLeft(20, '0') + "-" + s[(dash + 1)..].PadLeft(20, '0');
    }

    private static CloseRangeRoomSignal ToSignal(StreamEntry entry)
    {
        string Field(string name)
        {
            foreach (var v in entry.Values)
                if (v.Name == name) return v.Value.ToString();
            return "";
        }

        return new CloseRangeRoomSignal
        {
            Id = NormalizeId(entry.Id),
            Kind = Field("kind"),
            RoomGuid = Convert.FromBase64String(Field("room")),
            CharacterId = uint.Parse(Field("cid")),
            Capacity = byte.Parse(Field("cap")),
            Password = Convert.FromBase64String(Field("pw")),
            MemberCount = int.Parse(Field("mc")),
            NewMasterCharacterId = uint.Parse(Field("nm")),
            OwnerGone = Field("og") == "1",
            CreatedUtc = new DateTime(long.Parse(Field("ts")), DateTimeKind.Utc),
        };
    }

 /// <summary>Returns every signal in the stream, in insertion order.</summary>
    private async Task<List<CloseRangeRoomSignal>> ReadAllAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entries = await _redis.Db.StreamRangeAsync(StreamKey, "-", "+");
        return entries.Select(ToSignal).ToList();
    }

 /// <summary>Returns the room's latest open signal and the signals recorded after it, in insertion order.</summary>
    private static (CloseRangeRoomSignal Open, List<CloseRangeRoomSignal> After)? LatestOpen(
        List<CloseRangeRoomSignal> all, byte[] guid)
    {
        var index = all.FindLastIndex(s => s.Kind == CloseRangeRoomSignals.OpenKind && s.RoomGuid.AsSpan().SequenceEqual(guid));
        return index < 0 ? null : (all[index], all.GetRange(index + 1, all.Count - index - 1));
    }

    public async Task<CloseRangeRoomSignal?> FindOpenRoomAsync(ReadOnlyMemory<byte> roomGuid,
        CancellationToken ct = default)
    {
        var guid = roomGuid.ToArray();
        if (LatestOpen(await ReadAllAsync(ct), guid) is not { } found) return null;
        var (open, after) = found;

        
        
        var exits = after.Where(s => s.Kind == CloseRangeRoomSignals.ExitKind && s.RoomGuid.AsSpan().SequenceEqual(guid)).ToList();
        var owners = new HashSet<uint> { open.CharacterId };
        foreach (var e in exits)
            if (e.NewMasterCharacterId != 0) owners.Add(e.NewMasterCharacterId);
        var opens = after.Where(s => s.Kind == CloseRangeRoomSignals.OpenKind && owners.Contains(s.CharacterId));

        if (CloseRangeRoomSignals.CurrentOwner(open, exits.Concat(opens).OrderBy(s => s.Id, StringComparer.Ordinal))
                is not uint owner)
            return null;
        return new CloseRangeRoomSignal
        {
            Id = open.Id,
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
        if (LatestOpen(await ReadAllAsync(ct), guid) is not { } found) return null;
        var (open, after) = found;

        var ofRoom = after.Where(s => s.RoomGuid.AsSpan().SequenceEqual(guid)
            && (s.Kind == CloseRangeRoomSignals.JoinKind || s.Kind == CloseRangeRoomSignals.ExitKind)).ToList();
        var owners = new HashSet<uint> { open.CharacterId };
        foreach (var e in ofRoom)
            if (e.Kind == CloseRangeRoomSignals.ExitKind && e.NewMasterCharacterId != 0) owners.Add(e.NewMasterCharacterId);
        var opens = after.Where(s => s.Kind == CloseRangeRoomSignals.OpenKind && owners.Contains(s.CharacterId));

        return new CloseRangeRoomHistory(open, ofRoom.Concat(opens).OrderBy(s => s.Id, StringComparer.Ordinal).ToList());
    }

    public async Task SubscribeAsync(DateTime fromUtc, Func<CloseRangeRoomSignal, Task> onSignal,
        Action<Exception> onError, CancellationToken ct)
    {
        var fromMs = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        RedisValue lastId = $"{Math.Max(fromMs, 0)}-0";
        var inclusive = true; 

        while (!ct.IsCancellationRequested)
        {
            var delivered = false;
            try
            {
                var entries = await _redis.Db.StreamRangeAsync(StreamKey, lastId, "+", BatchSize);
                foreach (var entry in entries)
                {
                    if (!inclusive && entry.Id == lastId) continue;
                    lastId = entry.Id;
                    inclusive = false;
                    delivered = true;
                    await onSignal(ToSignal(entry));
                }
                if (entries.Length == BatchSize) continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                onError(ex);
                try { await Task.Delay(RetryDelay, ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            if (delivered) continue;
            try { await Task.Delay(PollDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
