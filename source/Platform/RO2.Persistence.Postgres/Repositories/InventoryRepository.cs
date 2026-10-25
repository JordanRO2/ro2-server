using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Inventory.Domain;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IInventoryRepository"/>. One row per <see cref="InventoryItem"/> in
/// <c>inventory_items</c> (flat: nothing nested is queried, so no child table); the item-id sequence is the
/// one-row-per-name <c>inventory_counters</c> table (the Mongo <c>inventory_counters</c> collection).
/// </summary>
/// <remarks>
/// Columns of <c>inventory_items</c> (the timed-enchant port reads and updates the same rows by these names):
/// <c>id</c> (bigint PK, the client-visible item uid), <c>character_id</c>, <c>item_id</c>, <c>count</c>,
/// <c>slot</c>, <c>storage_type</c>, <c>refine</c>, <c>element</c>, <c>durability</c>, <c>dye_color</c>,
/// <c>dye_color2</c>, <c>dye_color3</c>, <c>fx_enchant_index</c>, <c>enchant_reinforce_id</c>,
/// <c>enchant_seconds</c>, <c>cards_json</c>, <c>socket_punchers_json</c>, <c>bound</c>, <c>ability_value</c>,
/// <c>created_at</c>. Postgres has no unsigned integers: <c>uint</c>/<c>ushort</c>/<c>ulong</c> map to
/// <c>bigint</c> (the id through an unchecked cast), <c>byte</c>/<c>short</c> to <c>smallint</c>, <c>int</c> to
/// <c>integer</c>, <c>bool</c> to <c>boolean</c>; <c>created_at</c> is a UTC <c>timestamptz</c> written once and
/// never updated. The id is not an IDENTITY column: <see cref="GetNextIdAsync"/> must reserve an id without
/// inserting a row, and <see cref="AddItemAsync"/> accepts a pre-set id (a trade re-homes a row by uid).
/// <para>
/// Lowest-free-slot assignment is pick-then-insert, with the partial unique index
/// <see cref="SlotIndexName"/> (<c>character_id, storage_type, slot WHERE slot &gt;= 0</c>) as the atomic
/// arbiter: when another writer takes the picked slot first, the insert fails with SQLSTATE 23505 on that
/// index, the used slots are re-read and the pick is retried, up to <c>MaxSlotAssignAttempts</c> attempts,
/// after which the exception propagates (as Mongo's does). Any other unique violation (e.g. the id) propagates
/// at once.
/// </para>
/// </remarks>
public sealed class InventoryRepository : IInventoryRepository
{
    public const string ItemsTable = "inventory_items";
    public const string CountersTable = "inventory_counters";

    /// <summary>The <c>counter_name</c> of the item-id sequence row (the Mongo <c>CounterId</c>).</summary>
    public const string CounterId = "inventoryItemId";

    /// <summary>Name of the unique placement index.</summary>
    public const string SlotIndexName = "uq_inventory_char_storage_slot";

    private const int MaxSlotAssignAttempts = 5;

    // The 18 mutable columns, in the order BindMutable yields their values.
    private static readonly (string Name, NpgsqlDbType Type)[] MutableColumns =
    [
        ("character_id", NpgsqlDbType.Bigint),
        ("item_id", NpgsqlDbType.Bigint),
        ("count", NpgsqlDbType.Integer),
        ("slot", NpgsqlDbType.Smallint),
        ("storage_type", NpgsqlDbType.Smallint),
        ("refine", NpgsqlDbType.Smallint),
        ("element", NpgsqlDbType.Smallint),
        ("durability", NpgsqlDbType.Smallint),
        ("dye_color", NpgsqlDbType.Bigint),
        ("dye_color2", NpgsqlDbType.Bigint),
        ("dye_color3", NpgsqlDbType.Bigint),
        ("fx_enchant_index", NpgsqlDbType.Bigint),
        ("enchant_reinforce_id", NpgsqlDbType.Bigint),
        ("enchant_seconds", NpgsqlDbType.Bigint),
        ("cards_json", NpgsqlDbType.Text),
        ("socket_punchers_json", NpgsqlDbType.Text),
        ("bound", NpgsqlDbType.Boolean),
        ("ability_value", NpgsqlDbType.Bigint),
    ];

    private static readonly string SelectColumns =
        "id, " + string.Join(", ", MutableColumns.Select(c => c.Name)) + ", created_at";

    private static readonly string InsertSql =
        $"INSERT INTO {ItemsTable} (id, {string.Join(", ", MutableColumns.Select(c => c.Name))}, created_at) " +
        $"VALUES (@id, {string.Join(", ", MutableColumns.Select((_, i) => "@p" + i))}, @created)";

    private static readonly string UpdateSql =
        $"UPDATE {ItemsTable} SET {string.Join(", ", MutableColumns.Select((c, i) => $"{c.Name} = @p{i}"))} WHERE id = @id";

    // Seeded from the existing rows once per process; the seed is idempotent, so several processes seeding is harmless.
    private static int s_counterSeeded;

    private readonly PostgresDbContext _db;

    public InventoryRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM {ItemsTable} WHERE character_id = @c ORDER BY slot, id", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        return await ReadItemsAsync(cmd);
    }

    public async Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM {ItemsTable} WHERE character_id = @c AND storage_type = 1 ORDER BY slot, id", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        return await ReadItemsAsync(cmd);
    }

    public async Task<InventoryItem?> GetByIdAsync(ulong id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {SelectColumns} FROM {ItemsTable} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", unchecked((long)id));
        var rows = await ReadItemsAsync(cmd);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task AddItemAsync(InventoryItem item)
    {
        if (item.Id == 0)
            item.Id = await GetNextIdAsync();
        if (item.Slot >= 0)
        {
            await InsertAsync(item);
            return;
        }

        // Assign the lowest free slot for this character's storage of this type: the client places an item purely
        // by the (bag, slot) in its record. The unique index rejects a lost race, and the pick is retried.
        for (var attempt = 1; ; attempt++)
        {
            var used = await ReadUsedSlotsAsync(item.CharacterId, item.StorageType);
            item.Slot = LowestFreeSlot(used);
            try
            {
                await InsertAsync(item);
                return;
            }
            catch (PostgresException ex) when (IsSlotCollision(ex) && attempt < MaxSlotAssignAttempts)
            {
                // lost the slot to another writer: re-read and pick again
            }
        }
    }

    /// <summary>The lowest slot index (from 0) that is not in <paramref name="occupied"/>.</summary>
    public static short LowestFreeSlot(IEnumerable<int> occupied) => BagLayout.LowestFreeSlot(occupied);

    private static bool IsSlotCollision(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(ex.ConstraintName, SlotIndexName, StringComparison.Ordinal);

    public async Task RemoveItemAsync(ulong itemId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand($"DELETE FROM {ItemsTable} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", unchecked((long)itemId));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes the row only if it belongs to <paramref name="characterId"/>, so a forged or stale item id can never
    /// remove another character's row. True when a row was deleted.
    /// </summary>
    public async Task<bool> RemoveItemAsync(ulong itemId, uint characterId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {ItemsTable} WHERE id = @id AND character_id = @c RETURNING id", conn);
        cmd.Parameters.AddWithValue("id", unchecked((long)itemId));
        cmd.Parameters.AddWithValue("c", (long)characterId);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    /// <summary>
    /// Writes every mutable column of the row (all but <c>id</c> and <c>created_at</c>). Filtered by id only: a
    /// trade may legitimately change <c>character_id</c> on the row. No row, no effect; a slot that duplicates a
    /// placed one in the same storage is rejected by the unique index.
    /// </summary>
    public async Task UpdateItemAsync(InventoryItem item)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(UpdateSql, conn);
        cmd.Parameters.AddWithValue("id", unchecked((long)item.Id));
        BindMutable(cmd, item);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Atomically adds <paramref name="delta"/> to the stack's <c>count</c>, guarded in the same statement: the row
    /// must belong to <paramref name="characterId"/> and a negative delta must not take the count below 0. Returns
    /// the new count, or null when no row matched (not found, not the owner, or not enough in the stack), in which
    /// case nothing changed.
    /// </summary>
    public async Task<int?> AdjustCountAsync(ulong itemId, uint characterId, int delta)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var guard = delta < 0 ? " AND count >= @min" : "";
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {ItemsTable} SET count = count + @d WHERE id = @id AND character_id = @c{guard} RETURNING count", conn);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Integer) { Value = delta });
        cmd.Parameters.AddWithValue("id", unchecked((long)itemId));
        cmd.Parameters.AddWithValue("c", (long)characterId);
        if (delta < 0) cmd.Parameters.Add(new NpgsqlParameter("min", NpgsqlDbType.Bigint) { Value = -(long)delta });
        return await cmd.ExecuteScalarAsync() is int count ? count : null;
    }

    /// <summary>
    /// Re-homes the row in one <c>UPDATE ... WHERE id AND character_id = from RETURNING id</c> statement (a single
    /// transaction), so a stale or forged owner matches nothing and concurrent moves of the same row have exactly one
    /// winner. True when a row matched. A destination slot already taken violates the unique placement index and the
    /// statement throws, changing nothing.
    /// </summary>
    public async Task<bool> TryReassignAsync(ulong itemId, uint fromCharId, uint toCharId, byte toStorageType, short toSlot)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {ItemsTable} SET character_id = @to, storage_type = @st, slot = @s " +
            "WHERE id = @id AND character_id = @from RETURNING id", conn);
        cmd.Parameters.AddWithValue("to", (long)toCharId);
        cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Smallint) { Value = (short)toStorageType });
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Smallint) { Value = toSlot });
        cmd.Parameters.AddWithValue("id", unchecked((long)itemId));
        cmd.Parameters.AddWithValue("from", (long)fromCharId);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    /// <summary>
    /// Reserves and returns a fresh item id from the atomic <c>inventory_counters</c> sequence, so two concurrent
    /// callers always get distinct ids. It reserves: the number is consumed even if the caller never inserts a row
    /// with it. The first call in a process seeds the sequence from the highest existing row id (never lowering an
    /// already advanced counter), so ids keep growing past every row that exists today; the first id on an empty
    /// table is 1.
    /// </summary>
    public async Task<ulong> GetNextIdAsync()
    {
        await EnsureCounterSeededAsync();
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {CountersTable} (counter_name, seq) VALUES (@n, 1) " +
            $"ON CONFLICT (counter_name) DO UPDATE SET seq = {CountersTable}.seq + 1 RETURNING seq", conn);
        cmd.Parameters.AddWithValue("n", CounterId);
        return unchecked((ulong)(long)(await cmd.ExecuteScalarAsync())!);
    }

    private async Task EnsureCounterSeededAsync()
    {
        if (Volatile.Read(ref s_counterSeeded) == 1) return;
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {CountersTable} (counter_name, seq) " +
            $"SELECT @n, COALESCE(MAX(id), 0) FROM {ItemsTable} ON CONFLICT (counter_name) DO NOTHING", conn);
        cmd.Parameters.AddWithValue("n", CounterId);
        await cmd.ExecuteNonQueryAsync();
        Volatile.Write(ref s_counterSeeded, 1);
    }

    /// <summary>
    /// Creates the unique placement index on <c>(character_id, storage_type, slot)</c> for rows with a real slot
    /// (<c>slot &gt;= 0</c>; unplaced slot -1 rows are exempt). Idempotent (the base schema already creates it).
    /// Returns false (and creates nothing) when existing rows already collide, in which case run
    /// <see cref="RepairSlotsAsync"/> per affected character first.
    /// </summary>
    public async Task<bool> EnsureIndexesAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"CREATE UNIQUE INDEX IF NOT EXISTS {SlotIndexName} ON {ItemsTable} (character_id, storage_type, slot) WHERE slot >= 0", conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task<int> RepairSlotsAsync(uint characterId)
    {
        var items = await GetByCharacterAsync(characterId);
        var repaired = 0;
        // Per storage type: reassign every item that has no slot (Slot<0) OR that COLLIDES with a slot another
        // item already claimed, to the lowest free index. Processing by ascending slot (unplaced last) and then
        // by id makes the first claimant of a slot keep it. Equipment (StorageType 1) keeps its body-part slot.
        foreach (var grp in items.GroupBy(i => i.StorageType))
        {
            if (grp.Key == 1) continue; // equipment slot = equip position, never reassign
            foreach (var move in BagLayout.PlanSlotRepair(grp.Select(i => (i.Id, i.Slot))))
            {
                await using var conn = await _db.OpenConnectionAsync();
                await using var cmd = new NpgsqlCommand($"UPDATE {ItemsTable} SET slot = @s WHERE id = @id", conn);
                cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Smallint) { Value = move.Slot });
                cmd.Parameters.AddWithValue("id", unchecked((long)move.Id));
                await cmd.ExecuteNonQueryAsync();
                repaired++;
            }
        }
        return repaired;
    }

    private async Task InsertAsync(InventoryItem item)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("id", unchecked((long)item.Id));
        BindMutable(cmd, item);
        cmd.Parameters.Add(new NpgsqlParameter("created", NpgsqlDbType.TimestampTz) { Value = ToStoredUtc(item.CreatedAt) });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<int>> ReadUsedSlotsAsync(uint characterId, byte storageType)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT slot FROM {ItemsTable} WHERE character_id = @c AND storage_type = @t", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Smallint) { Value = (short)storageType });
        var used = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) used.Add(reader.GetInt16(0));
        return used;
    }

    private static async Task<IReadOnlyList<InventoryItem>> ReadItemsAsync(NpgsqlCommand cmd)
    {
        var items = new List<InventoryItem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new InventoryItem
            {
                Id = unchecked((ulong)reader.GetInt64(0)),
                CharacterId = (uint)reader.GetInt64(1),
                ItemId = (uint)reader.GetInt64(2),
                Count = reader.GetInt32(3),
                Slot = reader.GetInt16(4),
                StorageType = (byte)reader.GetInt16(5),
                Refine = (byte)reader.GetInt16(6),
                Element = (byte)reader.GetInt16(7),
                Durability = reader.GetInt16(8),
                DyeColor = (uint)reader.GetInt64(9),
                DyeColor2 = (uint)reader.GetInt64(10),
                DyeColor3 = (uint)reader.GetInt64(11),
                FxEnchantIndex = (uint)reader.GetInt64(12),
                EnchantReinforceId = (ushort)reader.GetInt64(13),
                EnchantSeconds = (ushort)reader.GetInt64(14),
                CardsJson = reader.IsDBNull(15) ? null : reader.GetString(15),
                SocketPunchersJson = reader.IsDBNull(16) ? null : reader.GetString(16),
                Bound = reader.GetBoolean(17),
                AbilityValue = reader.IsDBNull(18) ? null : (uint)reader.GetInt64(18),
                CreatedAt = reader.GetDateTime(19),
            });
        }
        return items;
    }

    private static void BindMutable(NpgsqlCommand cmd, InventoryItem i)
    {
        object?[] values =
        [
            (long)i.CharacterId, (long)i.ItemId, i.Count, i.Slot, (short)i.StorageType, (short)i.Refine,
            (short)i.Element, i.Durability, (long)i.DyeColor, (long)i.DyeColor2, (long)i.DyeColor3,
            (long)i.FxEnchantIndex, (long)i.EnchantReinforceId, (long)i.EnchantSeconds, i.CardsJson,
            i.SocketPunchersJson, i.Bound, i.AbilityValue is { } a ? (long)a : null,
        ];
        for (var n = 0; n < MutableColumns.Length; n++)
            cmd.Parameters.Add(new NpgsqlParameter("p" + n, MutableColumns[n].Type) { Value = values[n] ?? DBNull.Value });
    }

    /// <summary>UTC, truncated to the millisecond (the precision Mongo stores); never Kind=Unspecified for Npgsql.</summary>
    private static DateTime ToStoredUtc(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
