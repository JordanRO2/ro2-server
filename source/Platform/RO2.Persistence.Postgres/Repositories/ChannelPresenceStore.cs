using Npgsql;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IChannelPresenceStore"/>: the channel each player is on, shared by
/// the Login, Zone and Chat servers through the database. Mirrors the Mongo provider's behaviour exactly:
/// Login writes the account's selected handle (12005), the Zone overwrites it on a move (1421) and turns it
/// into the character's label at the 1417 that follows every world entry, and the Chat server reads the
/// labels when it builds a 23505.
/// </summary>
/// <remarks>
/// Two tables, one row per key, like the two Mongo collections: <c>channel_presence_accounts</c> (account id
/// to channel handle) and <c>channel_presence_characters</c> (character id to handle plus label). The Mongo
/// <c>ReplaceOne(..., IsUpsert)</c> becomes <c>INSERT ... ON CONFLICT (id) DO UPDATE</c>, so a write is a
/// full overwrite and last-writer-wins, as before. The <c>uint</c> ids and handles map to <c>bigint</c>
/// (Postgres has no unsigned integers) and read back with casts; <c>updated_utc</c> is a <c>timestamptz</c>
/// always written as <see cref="DateTime.UtcNow"/>. A null label is stored as the empty string, which reads
/// back the same as the Mongo document's missing/null label: left out of <see cref="GetLocationsAsync"/>.
/// The static label helpers are copied from the Mongo class so the Login/Zone handlers can use either provider.
/// </remarks>
public sealed class ChannelPresenceStore : IChannelPresenceStore
{
    public const string AccountTableName = "channel_presence_accounts";
    public const string CharacterTableName = "channel_presence_characters";

    private readonly PostgresDbContext _db;

    public ChannelPresenceStore(PostgresDbContext db) => _db = db;

 /// <summary>
 /// The label a channel list gives regular channel <paramref name="channel"/>: 'Channel N', what both our
 /// lists write and what retail's 12503 names its handles.
 /// </summary>
    public static string ChannelLabel(int channel) => $"Channel {channel}";

 /// <summary>
 /// The label of the channel whose handle is <paramref name="handle"/> among the
 /// <paramref name="channelCount"/> regular channels a list advertises, where
 /// <paramref name="handleOf"/>(N) is that list's handle of channel N; empty when the list has no such
 /// handle.
 /// </summary>
    public static string LabelOf(uint handle, int channelCount, Func<int, uint> handleOf)
    {
        for (int ch = 1; ch <= channelCount; ch++)
            if (handleOf(ch) == handle)
                return ChannelLabel(ch);
        return "";
    }

    public async Task SetAccountChannelAsync(uint accountId, uint channelHandle, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {AccountTableName} (id, channel_handle, updated_utc) VALUES (@id, @h, @u) " +
            "ON CONFLICT (id) DO UPDATE SET channel_handle = EXCLUDED.channel_handle, updated_utc = EXCLUDED.updated_utc",
            conn);
        cmd.Parameters.AddWithValue("id", (long)accountId);
        cmd.Parameters.AddWithValue("h", (long)channelHandle);
        cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<uint?> GetAccountChannelAsync(uint accountId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT channel_handle FROM {AccountTableName} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (uint)reader.GetInt64(0);
    }

    public async Task SetCharacterLocationAsync(uint characterId, uint channelHandle, string location, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {CharacterTableName} (id, channel_handle, location, updated_utc) VALUES (@id, @h, @l, @u) " +
            "ON CONFLICT (id) DO UPDATE SET channel_handle = EXCLUDED.channel_handle, location = EXCLUDED.location, " +
            "updated_utc = EXCLUDED.updated_utc",
            conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        cmd.Parameters.AddWithValue("h", (long)channelHandle);
        cmd.Parameters.AddWithValue("l", location ?? "");
        cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<uint, string>> GetLocationsAsync(IReadOnlyCollection<uint> characterIds, CancellationToken ct = default)
    {
        if (characterIds.Count == 0)
            return new Dictionary<uint, string>();

        var ids = new long[characterIds.Count];
        int i = 0;
        foreach (var id in characterIds)
            ids[i++] = id;

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT id, location FROM {CharacterTableName} WHERE id = ANY(@ids)", conn);
        cmd.Parameters.AddWithValue("ids", ids);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var byId = new Dictionary<uint, string>(ids.Length);
        while (await reader.ReadAsync(ct))
        {
            var location = reader.GetString(1);
            if (!string.IsNullOrEmpty(location))
                byId[(uint)reader.GetInt64(0)] = location;
        }
        return byId;
    }
}
