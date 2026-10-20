using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IMailRepository"/> over the <c>mail_messages</c> table and the
/// <c>mail_counters</c> sequence table (row <c>id = 'mailId'</c>). Mirrors the Mongo provider's behaviour:
/// one shared id sequence, uid = <c>0x0E4B4B8B_00000000 | id</c>, whole-row replace, newest-first inbox capped
/// at 200, and the spent-attachment delete as a single conditional statement.
/// </summary>
/// <remarks>
/// <para>A <see cref="MailMessage"/> is a flat record (no nested list is queried or CAS'd), so it maps to one row
/// with one column per property; the optional JSON snapshots (<c>AttachedItemCardsJson</c>,
/// <c>AttachedItemSocketPunchersJson</c>) are already strings and stay <c>text</c>. Postgres has no unsigned
/// integers: <c>ulong</c>/<c>uint</c>/<c>ushort</c> map to <c>bigint</c> (ulong through an unchecked cast,
/// safe because ids and uids stay below 2^63), <c>byte</c> to <c>smallint</c>. <c>SentAt</c> maps to
/// <c>timestamptz</c> and is always written as UTC truncated to the millisecond, the precision the Mongo
/// provider stores, so both providers return the same value and compare the same against <c>notBefore</c>.</para>
/// <para>The id sequence is a counter row bumped by <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c> in its own
/// statement, like the Mongo <c>FindOneAndUpdate</c> upsert: an id consumed by a failed insert is not reused.
/// <see cref="DeleteAsync"/> and <see cref="DeleteIfSpentAsync"/> are single <c>DELETE … RETURNING</c> statements
/// whose row-returned result is the "exactly one matched" signal.</para>
/// </remarks>
public sealed class MailRepository : IMailRepository
{
    public const string TableName = "mail_messages";
    public const string CounterTableName = "mail_counters";

    private const string CounterId = "mailId";
    private const int InboxLimit = 200;

    /// <summary>High dword of a mail uid, the fixed prefix of the 64-bit mail handle the client addresses a mail by.</summary>
    private const uint MailUidHighDword = 0x0E4B4B8B;

    // Column order is the contract shared by Values (write) and Read (read); index 0 is the primary key.
    private static readonly (string Name, NpgsqlDbType Type)[] Columns =
    {
        ("id", NpgsqlDbType.Bigint),
        ("uid", NpgsqlDbType.Bigint),
        ("receiver_char_id", NpgsqlDbType.Bigint),
        ("receiver_name", NpgsqlDbType.Text),
        ("sender_char_id", NpgsqlDbType.Bigint),
        ("sender_name", NpgsqlDbType.Text),
        ("title", NpgsqlDbType.Text),
        ("body", NpgsqlDbType.Text),
        ("attached_item_uid", NpgsqlDbType.Bigint),
        ("attached_item_id", NpgsqlDbType.Bigint),
        ("attached_item_count", NpgsqlDbType.Integer),
        ("attached_item_refine", NpgsqlDbType.Smallint),
        ("attached_item_durability", NpgsqlDbType.Smallint),
        ("attached_item_cards_json", NpgsqlDbType.Text),
        ("attached_item_element", NpgsqlDbType.Smallint),
        ("attached_item_dye_color", NpgsqlDbType.Bigint),
        ("attached_item_socket_punchers_json", NpgsqlDbType.Text),
        ("attached_item_dye_color2", NpgsqlDbType.Bigint),
        ("attached_item_dye_color3", NpgsqlDbType.Bigint),
        ("attached_item_fx_enchant_index", NpgsqlDbType.Bigint),
        ("attached_item_enchant_reinforce_id", NpgsqlDbType.Bigint),
        ("attached_item_enchant_seconds", NpgsqlDbType.Bigint),
        ("attached_item_bound", NpgsqlDbType.Boolean),
        ("attached_item_ability_value", NpgsqlDbType.Bigint),
        ("item_claimed", NpgsqlDbType.Boolean),
        ("gold", NpgsqlDbType.Bigint),
        ("gold_claimed", NpgsqlDbType.Boolean),
        ("is_read", NpgsqlDbType.Boolean),
        ("sent_at", NpgsqlDbType.TimestampTz),
        ("kind", NpgsqlDbType.Smallint),
        ("kind_item_id", NpgsqlDbType.Bigint),
        ("auction_price", NpgsqlDbType.Bigint),
    };

    private static readonly string ColumnList = string.Join(", ", Columns.Select(c => c.Name));

    private static readonly string InsertSql =
        $"INSERT INTO {TableName} ({ColumnList}) VALUES ({string.Join(", ", Columns.Select((_, i) => "@p" + i))})";

    // Every column except the primary key is overwritten; the row is located by id (= parameter p0).
    private static readonly string UpdateSql =
        $"UPDATE {TableName} SET {string.Join(", ", Columns.Skip(1).Select((c, i) => $"{c.Name} = @p{i + 1}"))} WHERE id = @p0";

    private readonly PostgresDbContext _db;

    public MailRepository(PostgresDbContext db) => _db = db;

    /// <summary>uid = 0x0E4B4B8B_00000000 | id; the high dword is a fixed prefix of the 64-bit mail handle; the low dword is unique.</summary>
    private static ulong MakeUid(ulong id) => ((ulong)MailUidHighDword << 32) | (uint)id;

    /// <summary>Atomic increasing id from the <c>mail_counters</c> row (created on first use, first id 1).</summary>
    private async Task<ulong> NextIdAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {CounterTableName} (id, seq) VALUES (@id, 1) " +
            $"ON CONFLICT (id) DO UPDATE SET seq = {CounterTableName}.seq + 1 RETURNING seq", conn);
        cmd.Parameters.AddWithValue("id", CounterId);
        var seq = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return unchecked((ulong)seq);
    }

    public async Task<MailMessage> AddAsync(MailMessage draft, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        var id = await NextIdAsync(conn, ct);
        draft.Id = id;
        draft.Uid = MakeUid(id);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        Bind(cmd, draft);
        await cmd.ExecuteNonQueryAsync(ct);
        return draft;
    }

    public async Task<IReadOnlyList<MailMessage>> GetInboxAsync(uint receiverCharId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        // id DESC breaks sent_at ties deterministically (Mongo leaves ties unordered).
        await using var cmd = new NpgsqlCommand(
            $"SELECT {ColumnList} FROM {TableName} WHERE receiver_char_id = @r " +
            $"ORDER BY sent_at DESC, id DESC LIMIT {InboxLimit}", conn);
        cmd.Parameters.AddWithValue("r", (long)receiverCharId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<MailMessage>();
        while (await reader.ReadAsync(ct)) list.Add(Read(reader));
        return list;
    }

    public async Task<MailMessage?> GetByUidAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {ColumnList} FROM {TableName} WHERE receiver_char_id = @r AND uid = @u LIMIT 1", conn);
        cmd.Parameters.AddWithValue("r", (long)receiverCharId);
        cmd.Parameters.AddWithValue("u", unchecked((long)uid));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<long> CountUnreadAsync(uint receiverCharId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM {TableName} WHERE receiver_char_id = @r AND NOT is_read", conn);
        cmd.Parameters.AddWithValue("r", (long)receiverCharId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> CountLiveInboxAsync(uint receiverCharId, DateTime notBefore, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM {TableName} WHERE receiver_char_id = @r AND sent_at >= @nb", conn);
        cmd.Parameters.AddWithValue("r", (long)receiverCharId);
        cmd.Parameters.Add(new NpgsqlParameter("nb", NpgsqlDbType.TimestampTz) { Value = ToStoredUtc(notBefore) });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ReplaceAsync(MailMessage mail, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        // No row with that id: nothing happens (no upsert), as Mongo's ReplaceOne without IsUpsert.
        await using var cmd = new NpgsqlCommand(UpdateSql, conn);
        Bind(cmd, mail);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE receiver_char_id = @r AND uid = @u RETURNING id", conn);
        cmd.Parameters.AddWithValue("r", (long)receiverCharId);
        cmd.Parameters.AddWithValue("u", unchecked((long)uid));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public Task<MailMessage?> TryClaimItemAsync(uint receiverCharId, ulong uid, CancellationToken ct = default) =>
        ClaimAsync("item_claimed", "item_claimed = false AND attached_item_id <> 0", receiverCharId, uid, ct);

    public Task<MailMessage?> TryClaimGoldAsync(uint receiverCharId, ulong uid, CancellationToken ct = default) =>
        ClaimAsync("gold_claimed", "gold_claimed = false AND gold > 0", receiverCharId, uid, ct);

    // One guarded UPDATE ... RETURNING: the flag flips only while the guard still holds, so of any number of racing
    // claims exactly one gets a row back (the returned row is the post-claim view).
    private async Task<MailMessage?> ClaimAsync(string flagColumn, string guard, uint receiverCharId, ulong uid,
        CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET {flagColumn} = true WHERE receiver_char_id = @r AND uid = @u AND {guard} " +
            $"RETURNING {ColumnList}", conn);
        cmd.Parameters.AddWithValue("r", (long)receiverCharId);
        cmd.Parameters.AddWithValue("u", unchecked((long)uid));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<bool> DeleteIfSpentAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        // Same predicate as the Mongo filter, evaluated atomically with the delete: no item pending
        // (AttachedItemId == 0 or ItemClaimed) and no zeny pending (Gold <= 0 or GoldClaimed).
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE receiver_char_id = @r AND uid = @u " +
            "AND (attached_item_id = 0 OR item_claimed) AND (gold <= 0 OR gold_claimed) RETURNING id", conn);
        cmd.Parameters.AddWithValue("r", (long)receiverCharId);
        cmd.Parameters.AddWithValue("u", unchecked((long)uid));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>UTC, truncated to the millisecond (the precision Mongo stores); never Kind=Unspecified for Npgsql.</summary>
    private static DateTime ToStoredUtc(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    private static void Bind(NpgsqlCommand cmd, MailMessage m)
    {
        var values = Values(m);
        for (var i = 0; i < Columns.Length; i++)
            cmd.Parameters.Add(new NpgsqlParameter("p" + i, Columns[i].Type) { Value = values[i] ?? DBNull.Value });
    }

    /// <summary>The row values in <see cref="Columns"/> order, each boxed as the column's CLR type.</summary>
    private static object?[] Values(MailMessage m) => new object?[]
    {
        unchecked((long)m.Id),
        unchecked((long)m.Uid),
        (long)m.ReceiverCharId,
        m.ReceiverName ?? string.Empty,
        (long)m.SenderCharId,
        m.SenderName ?? string.Empty,
        m.Title ?? string.Empty,
        m.Body ?? string.Empty,
        unchecked((long)m.AttachedItemUid),
        (long)m.AttachedItemId,
        m.AttachedItemCount,
        (short)m.AttachedItemRefine,
        m.AttachedItemDurability,
        m.AttachedItemCardsJson,
        (short)m.AttachedItemElement,
        (long)m.AttachedItemDyeColor,
        m.AttachedItemSocketPunchersJson,
        (long)m.AttachedItemDyeColor2,
        (long)m.AttachedItemDyeColor3,
        (long)m.AttachedItemFxEnchantIndex,
        (long)m.AttachedItemEnchantReinforceId,
        (long)m.AttachedItemEnchantSeconds,
        m.AttachedItemBound,
        m.AttachedItemAbilityValue is { } av ? (long)av : null,
        m.ItemClaimed,
        m.Gold,
        m.GoldClaimed,
        m.IsRead,
        ToStoredUtc(m.SentAt),
        (short)m.Kind,
        (long)m.KindItemId,
        m.AuctionPrice,
    };

    private static MailMessage Read(NpgsqlDataReader r) => new()
    {
        Id = unchecked((ulong)r.GetInt64(0)),
        Uid = unchecked((ulong)r.GetInt64(1)),
        ReceiverCharId = (uint)r.GetInt64(2),
        ReceiverName = r.GetString(3),
        SenderCharId = (uint)r.GetInt64(4),
        SenderName = r.GetString(5),
        Title = r.GetString(6),
        Body = r.GetString(7),
        AttachedItemUid = unchecked((ulong)r.GetInt64(8)),
        AttachedItemId = (uint)r.GetInt64(9),
        AttachedItemCount = r.GetInt32(10),
        AttachedItemRefine = (byte)r.GetInt16(11),
        AttachedItemDurability = r.GetInt16(12),
        AttachedItemCardsJson = r.IsDBNull(13) ? null : r.GetString(13),
        AttachedItemElement = (byte)r.GetInt16(14),
        AttachedItemDyeColor = (uint)r.GetInt64(15),
        AttachedItemSocketPunchersJson = r.IsDBNull(16) ? null : r.GetString(16),
        AttachedItemDyeColor2 = (uint)r.GetInt64(17),
        AttachedItemDyeColor3 = (uint)r.GetInt64(18),
        AttachedItemFxEnchantIndex = (uint)r.GetInt64(19),
        AttachedItemEnchantReinforceId = (ushort)r.GetInt64(20),
        AttachedItemEnchantSeconds = (ushort)r.GetInt64(21),
        AttachedItemBound = r.GetBoolean(22),
        AttachedItemAbilityValue = r.IsDBNull(23) ? null : (uint)r.GetInt64(23),
        ItemClaimed = r.GetBoolean(24),
        Gold = r.GetInt64(25),
        GoldClaimed = r.GetBoolean(26),
        IsRead = r.GetBoolean(27),
        SentAt = DateTime.SpecifyKind(r.GetDateTime(28), DateTimeKind.Utc),
        Kind = (byte)r.GetInt16(29),
        KindItemId = (uint)r.GetInt64(30),
        AuctionPrice = r.GetInt64(31),
    };
}
