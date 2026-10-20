using Npgsql;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IAccountGiftRepository"/>. The Mongo provider keeps one
/// <c>account_gifts</c> document per login account (<c>_id</c> = the account id) carrying the embedded gift list and
/// the daily attendance; here that document is split into one row per account (<c>account_gift_boxes</c>: the
/// attendance counters and <c>updated_at</c>) and a child table (<c>account_gift_entries</c>: one row per gift),
/// because the gifts are individually pulled (by guid on a claim, by filing time on expiry) and the box and its
/// attendance must change together in one transaction.
/// </summary>
/// <remarks>
/// <para>The list order Mongo got from array position is the child table's identity column <c>seq</c>, so the stable
/// newest-first ordering of <see cref="AccountGiftRules.NewestFirst"/> breaks ties exactly as before. Expiry stays
/// in C#: <see cref="GetLiveAsync"/> reads the gifts, judges them with <see cref="AccountGiftRules.IsLive"/> and
/// purges the dead ones with <c>received_utc &lt; cutoff</c>, the same cutoff the Mongo <c>$pull</c> used (and, as
/// there, without touching <c>updated_at</c>).</para>
/// <para>The attendance compare-and-set is a single statement. For a never-attended account
/// (<c>lastAttendedDaySeen == 0</c>) it is <c>INSERT … ON CONFLICT DO UPDATE … WHERE last_attended_day = 0
/// RETURNING</c>: no box inserts it, a box that never attended is updated, and a box that already attended returns
/// no row (Mongo's duplicate-key on the upsert). Otherwise it is <c>UPDATE … WHERE last_attended_day = @seen
/// RETURNING</c>. The reward gift is filed in the same transaction only when that row came back.</para>
/// <para><see cref="TryTakeAsync"/> keeps the documented Mongo limitation: it sets <c>updated_at</c> on the account's
/// box and reports that, so it is true whenever the account has a box, even when the guid was not in it.</para>
/// <para><c>uint</c> account and item ids and the <c>ushort</c> count map to <c>bigint</c>/<c>integer</c>, the
/// <c>byte</c> source to <c>smallint</c>; every <see cref="DateTime"/> is passed with Kind=Utc for
/// <c>timestamptz</c>. Postgres keeps microseconds where BSON dates kept milliseconds.</para>
/// </remarks>
public sealed class AccountGiftRepository : IAccountGiftRepository
{
    public const string BoxesTable = "account_gift_boxes";
    public const string EntriesTable = "account_gift_entries";

    private const string EntryColumns = "guid, item_id, count, received_utc, source";

    private readonly PostgresDbContext _db;

    public AccountGiftRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<AccountGift>> GetLiveAsync(uint accountId, DateTime nowLocal, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        var gifts = new List<AccountGift>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {EntryColumns} FROM {EntriesTable} WHERE account_id = @a ORDER BY seq", conn))
        {
            cmd.Parameters.AddWithValue("a", (long)accountId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) gifts.Add(ReadGift(reader));
        }

        if (gifts.Any(g => !AccountGiftRules.IsLive(g, nowLocal)))
        {
            var cutoffUtc = OldestLiveLocal(nowLocal).ToUniversalTime();
            await using var cmd = new NpgsqlCommand(
                $"DELETE FROM {EntriesTable} WHERE account_id = @a AND received_utc < @c", conn);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            cmd.Parameters.AddWithValue("c", cutoffUtc);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return AccountGiftRules.NewestFirst(gifts, nowLocal);
    }

    public async Task<int> FileAsync(uint accountId, IReadOnlyList<AccountGift> gifts, DateTime nowLocal, CancellationToken ct = default)
    {
        if (gifts.Count > 0)
        {
            await using var conn = await _db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            
            await using (var cmd = new NpgsqlCommand(
                $"INSERT INTO {BoxesTable} (account_id, updated_at) VALUES (@a, @u) " +
                "ON CONFLICT (account_id) DO UPDATE SET updated_at = EXCLUDED.updated_at", conn, tx))
            {
                cmd.Parameters.AddWithValue("a", (long)accountId);
                cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var gift in gifts) await InsertGiftAsync(conn, tx, accountId, gift, ct);
            await tx.CommitAsync(ct);
        }
        return (await GetLiveAsync(accountId, nowLocal, ct)).Count;
    }

    public async Task<bool> TryTakeAsync(uint accountId, string guid, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        
        
        await using (var cmd = new NpgsqlCommand(
            $"UPDATE {BoxesTable} SET updated_at = @u WHERE account_id = @a RETURNING account_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            if (await cmd.ExecuteScalarAsync(ct) is null) return false; 
        }

        
        await using (var cmd = new NpgsqlCommand(
            $"DELETE FROM {EntriesTable} WHERE account_id = @a AND guid = @g", conn, tx))
        {
            cmd.Parameters.AddWithValue("a", (long)accountId);
            cmd.Parameters.AddWithValue("g", guid ?? "");
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<AccountAttendance> GetAttendanceAsync(uint accountId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT attended_days, last_attended_day, attendance_streak_broken_after FROM {BoxesTable} WHERE account_id = @a", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AccountAttendance(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))
            : new AccountAttendance(0, 0, 0);
    }

    public async Task<bool> TryRecordAttendanceAsync(uint accountId, int lastAttendedDaySeen, int today, int attendedDays,
        int streakBrokenAfter, AccountGift? reward, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        
        
        var sql = lastAttendedDaySeen == 0
            ? $"INSERT INTO {BoxesTable} (account_id, attended_days, last_attended_day, attendance_streak_broken_after, updated_at) " +
              "VALUES (@a, @d, @t, @s, @u) " +
              "ON CONFLICT (account_id) DO UPDATE SET attended_days = EXCLUDED.attended_days, " +
              "last_attended_day = EXCLUDED.last_attended_day, " +
              "attendance_streak_broken_after = EXCLUDED.attendance_streak_broken_after, updated_at = EXCLUDED.updated_at " +
              $"WHERE {BoxesTable}.last_attended_day = 0 RETURNING account_id"
            : $"UPDATE {BoxesTable} SET attended_days = @d, last_attended_day = @t, " +
              "attendance_streak_broken_after = @s, updated_at = @u " +
              "WHERE account_id = @a AND last_attended_day = @seen RETURNING account_id";
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.AddWithValue("a", (long)accountId);
            cmd.Parameters.AddWithValue("d", attendedDays);
            cmd.Parameters.AddWithValue("t", today);
            cmd.Parameters.AddWithValue("s", streakBrokenAfter);
            cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
            if (lastAttendedDaySeen != 0) cmd.Parameters.AddWithValue("seen", lastAttendedDaySeen);
            if (await cmd.ExecuteScalarAsync(ct) is null) return false; 
        }

        
        if (reward is not null) await InsertGiftAsync(conn, tx, accountId, reward, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task InsertGiftAsync(NpgsqlConnection conn, NpgsqlTransaction tx, uint accountId,
        AccountGift gift, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {EntriesTable} (account_id, {EntryColumns}) VALUES (@a, @g, @i, @c, @r, @s)", conn, tx);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("g", gift.Guid ?? "");
        cmd.Parameters.AddWithValue("i", (long)gift.ItemId);
        cmd.Parameters.AddWithValue("c", (int)gift.Count);
        cmd.Parameters.AddWithValue("r", ToUtc(gift.ReceivedUtc));
        cmd.Parameters.AddWithValue("s", (short)gift.Source);
        await cmd.ExecuteNonQueryAsync(ct);
    }

 /// <summary>Reads one row selected with <c>EntryColumns</c>.</summary>
    private static AccountGift ReadGift(NpgsqlDataReader reader) => new()
    {
        Guid = reader.GetString(0),
        ItemId = (uint)reader.GetInt64(1),
        Count = (ushort)reader.GetInt32(2),
        ReceivedUtc = reader.GetDateTime(3),
        Source = (byte)reader.GetInt16(4),
    };

    private static DateTime OldestLiveLocal(DateTime nowLocal) => nowLocal.Date.AddDays(-AccountGiftRules.DaysKept);

 /// <summary>Npgsql only accepts Kind=Utc for <c>timestamptz</c>; normalise whatever the caller built.</summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
