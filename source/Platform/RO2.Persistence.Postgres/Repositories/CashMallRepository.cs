using Npgsql;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="ICashMallRepository"/>. The Mongo provider keeps one
/// <c>cash_mall_accounts</c> document per login account carrying the balance plus two embedded lists (the
/// storage-box entries and the purchase history); here that document is split into one row per account
/// (<c>cash_mall_accounts</c>: balance) and two child tables (<c>cash_mall_entries</c>,
/// <c>cash_mall_purchases</c>), because the box entries are individually compare-and-set (units taken and
/// returned) and both lists are filtered and ordered. The two private id sequences of
/// <c>cash_mall_counters</c> become one row per counter name.
/// </summary>
/// <remarks>
/// Mongo's single-document atomicity is reproduced with a transaction that always locks the account row
/// first (the conditional <c>UPDATE</c> itself, or <c>FOR NO KEY UPDATE</c>), so a purchase, a refund and a
/// take serialise per account exactly as they did per document. The list order Mongo got from array position
/// is the child tables' identity column <c>seq</c>. <c>uint</c>/<c>ushort</c> map to <c>bigint</c>, the box
/// number (<c>byte</c>) to <c>smallint</c>, and every <see cref="DateTime"/> is passed with Kind=Utc for
/// <c>timestamptz</c>. Note Postgres keeps microseconds where BSON dates kept milliseconds.
/// </remarks>
public sealed class CashMallRepository : ICashMallRepository
{
    public const string AccountsTable = "cash_mall_accounts";
    public const string EntriesTable = "cash_mall_entries";
    public const string PurchasesTable = "cash_mall_purchases";
    public const string CountersTable = "cash_mall_counters";

    private const string EntryColumns = "entry_id, box, item_id, units, filed_utc, sender_name";
    private const string PurchaseColumns =
        "order_id, item_id, product_id, quantity, unit_price, bought_utc, gift, sender_name, recipient_name, recipient_account_id";

    private readonly PostgresDbContext _db;

    public CashMallRepository(PostgresDbContext db) => _db = db;

    public async Task<long> GetBalanceAsync(uint accountId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT balance FROM {AccountsTable} WHERE account_id = @a", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        return await cmd.ExecuteScalarAsync(ct) is long balance ? balance : 0;
    }

    public async Task<IReadOnlyList<CashMallBoxEntry>> GetBoxAsync(uint accountId, byte box, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {EntryColumns} FROM {EntriesTable} WHERE account_id = @a AND box = @b AND units > 0 ORDER BY seq", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("b", (short)box);
        var entries = new List<CashMallBoxEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) entries.Add(ReadEntry(reader));
        
        return CashMallRules.LiveEntries(entries, box);
    }

    public async Task<CashMallBoxEntry?> GetBoxEntryAsync(uint accountId, uint entryId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {EntryColumns} FROM {EntriesTable} WHERE account_id = @a AND entry_id = @e AND units > 0 ORDER BY seq LIMIT 1", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("e", (long)entryId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

    public async Task<(int Purchased, int Gifts)> GetBoxCountsAsync(uint accountId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT box, count(*) FROM {EntriesTable} WHERE account_id = @a AND units > 0 GROUP BY box", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        int purchased = 0, gifts = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var box = reader.GetInt16(0);
            var n = (int)reader.GetInt64(1);
            if (box == CashMallRules.BoxPurchased) purchased = n;
            else if (box == CashMallRules.BoxGift) gifts = n;
        }
        return (purchased, gifts);
    }

    public async Task<IReadOnlyList<CashMallPurchase>> GetPurchaseHistoryAsync(uint accountId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {PurchaseColumns} FROM {PurchasesTable} WHERE account_id = @a ORDER BY seq", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        var history = new List<CashMallPurchase>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            history.Add(new CashMallPurchase
            {
                OrderId = (uint)reader.GetInt64(0),
                ItemId = (uint)reader.GetInt64(1),
                ProductId = (uint)reader.GetInt64(2),
                Quantity = (ushort)reader.GetInt64(3),
                UnitPrice = reader.GetInt64(4),
                BoughtUtc = reader.GetDateTime(5),
                Gift = reader.GetBoolean(6),
                SenderName = reader.GetString(7),
                RecipientName = reader.GetString(8),
                RecipientAccountId = (uint)reader.GetInt64(9),
            });
        }
        return history;
    }

    public async Task<uint[]> ReserveIdsAsync(string counter, int count, CancellationToken ct = default)
    {
        if (count <= 0) return Array.Empty<uint>();
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {CountersTable} (counter_name, seq) VALUES (@n, @c) " +
            $"ON CONFLICT (counter_name) DO UPDATE SET seq = {CountersTable}.seq + EXCLUDED.seq RETURNING seq", conn);
        cmd.Parameters.AddWithValue("n", counter);
        cmd.Parameters.AddWithValue("c", (long)count);
        var last = (long)(await cmd.ExecuteScalarAsync(ct))!;
        var ids = new uint[count];
        for (int i = 0; i < count; i++) ids[i] = (uint)(last - count + 1 + i);
        return ids;
    }

    public async Task<long?> TryPurchaseAsync(uint accountId, long total, IReadOnlyList<CashMallPurchase> log,
        IReadOnlyList<CashMallBoxEntry> ownEntries, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        
        long after;
        await using (var cmd = new NpgsqlCommand(
            $"UPDATE {AccountsTable} SET balance = balance - @t, updated_at = @u " +
            "WHERE account_id = @a AND balance >= @t RETURNING balance", conn, tx))
        {
            cmd.Parameters.AddWithValue("t", total);
            cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            if (await cmd.ExecuteScalarAsync(ct) is not long balance) return null; 
            after = balance;
        }

        foreach (var purchase in log) await InsertPurchaseAsync(conn, tx, accountId, purchase, ct);
        foreach (var entry in ownEntries) await InsertEntryAsync(conn, tx, accountId, entry, ct);
        await tx.CommitAsync(ct);
        return after;
    }

    public async Task RefundAsync(uint accountId, long total, uint[] orderIds, uint[] entryIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int matched;
        await using (var cmd = new NpgsqlCommand(
            $"UPDATE {AccountsTable} SET balance = balance + @t, updated_at = @u WHERE account_id = @a", conn, tx))
        {
            cmd.Parameters.AddWithValue("t", total);
            cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            matched = await cmd.ExecuteNonQueryAsync(ct);
        }
        if (matched == 0) return; 

        if (orderIds.Length > 0)
        {
            await using var cmd = new NpgsqlCommand(
                $"DELETE FROM {PurchasesTable} WHERE account_id = @a AND order_id = ANY(@ids)", conn, tx);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            cmd.Parameters.AddWithValue("ids", orderIds.Select(i => (long)i).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (entryIds.Length > 0)
        {
            await using var cmd = new NpgsqlCommand(
                $"DELETE FROM {EntriesTable} WHERE account_id = @a AND entry_id = ANY(@ids)", conn, tx);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            cmd.Parameters.AddWithValue("ids", entryIds.Select(i => (long)i).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task FileGiftAsync(uint accountId, IReadOnlyList<CashMallBoxEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        
        await using (var cmd = new NpgsqlCommand(
            $"INSERT INTO {AccountsTable} (account_id, balance, updated_at) VALUES (@a, 0, @u) " +
            "ON CONFLICT (account_id) DO UPDATE SET updated_at = EXCLUDED.updated_at", conn, tx))
        {
            cmd.Parameters.AddWithValue("a", (long)accountId);
            cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var entry in entries) await InsertEntryAsync(conn, tx, accountId, entry, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<CashMallBoxEntry?> TryTakeUnitsAsync(uint accountId, uint entryId, int units, CancellationToken ct = default)
    {
        if (units <= 0) return null;
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        
        await using (var cmd = new NpgsqlCommand(
            $"SELECT 1 FROM {AccountsTable} WHERE account_id = @a FOR NO KEY UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("a", (long)accountId);
            if (await cmd.ExecuteScalarAsync(ct) is null) return null;
        }

        
        
        CashMallBoxEntry entry;
        await using (var cmd = new NpgsqlCommand(
            $"UPDATE {EntriesTable} SET units = units - @u " +
            $"WHERE seq = (SELECT seq FROM {EntriesTable} WHERE account_id = @a AND entry_id = @e AND units >= @u ORDER BY seq LIMIT 1) " +
            "AND units >= @u " +
            $"RETURNING {EntryColumns}", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", units);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            cmd.Parameters.AddWithValue("e", (long)entryId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            entry = ReadEntry(reader);
            entry.Count += units;
        }

        await using (var cmd = new NpgsqlCommand(
            $"UPDATE {AccountsTable} SET updated_at = @u WHERE account_id = @a", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (entry.Count == units)
        {
            
            await using var cmd = new NpgsqlCommand(
                $"DELETE FROM {EntriesTable} WHERE account_id = @a AND entry_id = @e AND units <= 0", conn, tx);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            cmd.Parameters.AddWithValue("e", (long)entryId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return entry;
    }

    public async Task ReturnUnitsAsync(uint accountId, CashMallBoxEntry taken, int units, CancellationToken ct = default)
    {
        if (units <= 0) return;
        int matched;
        await using (var conn = await _db.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(
            $"UPDATE {EntriesTable} SET units = units + @u " +
            $"WHERE seq = (SELECT seq FROM {EntriesTable} WHERE account_id = @a AND entry_id = @e ORDER BY seq LIMIT 1)", conn))
        {
            cmd.Parameters.AddWithValue("u", units);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            cmd.Parameters.AddWithValue("e", (long)taken.EntryId);
            matched = await cmd.ExecuteNonQueryAsync(ct);
        }
        if (matched == 0)
            await FileGiftAsync(accountId, new[]
            {
                new CashMallBoxEntry
                {
                    EntryId = taken.EntryId, Box = taken.Box, ItemId = taken.ItemId, Count = units,
                    FiledUtc = taken.FiledUtc, SenderName = taken.SenderName,
                },
            }, ct);
    }

    private static async Task InsertEntryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, uint accountId,
        CashMallBoxEntry entry, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {EntriesTable} (account_id, {EntryColumns}) VALUES (@a, @e, @b, @i, @u, @f, @s)", conn, tx);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("e", (long)entry.EntryId);
        cmd.Parameters.AddWithValue("b", (short)entry.Box);
        cmd.Parameters.AddWithValue("i", (long)entry.ItemId);
        cmd.Parameters.AddWithValue("u", entry.Count);
        cmd.Parameters.AddWithValue("f", ToUtc(entry.FiledUtc));
        cmd.Parameters.AddWithValue("s", entry.SenderName ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertPurchaseAsync(NpgsqlConnection conn, NpgsqlTransaction tx, uint accountId,
        CashMallPurchase purchase, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {PurchasesTable} (account_id, {PurchaseColumns}) VALUES (@a, @o, @i, @p, @q, @up, @bt, @g, @s, @rn, @ra)",
            conn, tx);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("o", (long)purchase.OrderId);
        cmd.Parameters.AddWithValue("i", (long)purchase.ItemId);
        cmd.Parameters.AddWithValue("p", (long)purchase.ProductId);
        cmd.Parameters.AddWithValue("q", (long)purchase.Quantity);
        cmd.Parameters.AddWithValue("up", purchase.UnitPrice);
        cmd.Parameters.AddWithValue("bt", ToUtc(purchase.BoughtUtc));
        cmd.Parameters.AddWithValue("g", purchase.Gift);
        cmd.Parameters.AddWithValue("s", purchase.SenderName ?? "");
        cmd.Parameters.AddWithValue("rn", purchase.RecipientName ?? "");
        cmd.Parameters.AddWithValue("ra", (long)purchase.RecipientAccountId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

 /// <summary>Reads one row selected with <c>EntryColumns</c>.</summary>
    private static CashMallBoxEntry ReadEntry(NpgsqlDataReader reader) => new()
    {
        EntryId = (uint)reader.GetInt64(0),
        Box = (byte)reader.GetInt16(1),
        ItemId = (uint)reader.GetInt64(2),
        Count = reader.GetInt32(3),
        FiledUtc = reader.GetDateTime(4),
        SenderName = reader.GetString(5),
    };

 /// <summary>Npgsql only accepts Kind=Utc for <c>timestamptz</c>; normalise whatever the caller built.</summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
