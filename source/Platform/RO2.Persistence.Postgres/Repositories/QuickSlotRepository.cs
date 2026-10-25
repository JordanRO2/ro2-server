using Npgsql;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Characters.Domain;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IQuickSlotRepository"/>: a character's quick-slot bar in the
/// <c>character_quickslots</c> table. The Mongo provider kept one document per occupied position, keyed
/// logically on (character, page, slot); here each occupied position is one row with the primary key
/// (<c>character_id</c>, <c>page</c>, <c>slot</c>), so "place an entry, replacing whatever was there" is a
/// single <c>INSERT … ON CONFLICT DO UPDATE</c>. No nested document exists, so no jsonb column is needed.
/// </summary>
/// <remarks>
/// <para>
/// Postgres has no unsigned integers: <c>uint</c> character and entry ids map to <c>bigint</c>,
/// <c>byte</c> page/slot/entry type to <c>smallint</c>, <c>ushort</c> amount to <c>integer</c>. The
/// <c>ulong</c> item uid and row id are stored as <c>bigint</c> through an unchecked reinterpreting cast, which
/// round-trips every 64-bit value bit-for-bit (values above <see cref="long.MaxValue"/> read back identical).
/// </para>
/// <para>
/// The row <see cref="CharacterQuickSlot.Id"/> is assigned on first insert only (<c>DateTime.UtcNow.Ticks</c>,
/// exactly the Mongo <c>SetOnInsert</c>) and left untouched when a later <see cref="SetAsync"/> replaces the
/// occupant; the id on the entry passed in is ignored, as in Mongo. Reads are ordered by page then slot.
/// </para>
/// <para>
/// <see cref="SwapAsync"/> keeps the Mongo semantics (the swapped entries are deleted and re-inserted at
/// their exchanged positions, whichever side is empty stays empty, swapping a position with itself leaves the
/// entry in place) but runs as one transaction: a single <c>DELETE … RETURNING</c> fetches and removes both
/// positions, then the exchanged rows are upserted, so a concurrent reader never sees a half-swapped bar.
/// </para>
/// </remarks>
public sealed class QuickSlotRepository : IQuickSlotRepository
{
    public const string TableName = "character_quickslots";

    private const string Columns = "id, character_id, page, slot, entry_type, entry_id, amount, item_uid";

    private readonly PostgresDbContext _db;

    public QuickSlotRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<CharacterQuickSlot>> GetByCharacterAsync(uint characterId)
    {
        var list = new List<CharacterQuickSlot>();
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM {TableName} WHERE character_id = @c ORDER BY page, slot", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(ReadEntry(reader));
        return list;
    }

    public async Task SetAsync(CharacterQuickSlot entry)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await UpsertAsync(conn, null, entry);
    }

    public async Task ClearAsync(uint characterId, byte page, byte slot)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE character_id = @c AND page = @p AND slot = @s", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("p", (short)page);
        cmd.Parameters.AddWithValue("s", (short)slot);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SwapAsync(uint characterId, byte pageA, byte slotA, byte pageB, byte slotB)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        
        var removed = new List<CharacterQuickSlot>();
        await using (var del = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE character_id = @c AND " +
            "((page = @pa AND slot = @sa) OR (page = @pb AND slot = @sb)) " +
            $"RETURNING {Columns}", conn, tx))
        {
            del.Parameters.AddWithValue("c", (long)characterId);
            del.Parameters.AddWithValue("pa", (short)pageA);
            del.Parameters.AddWithValue("sa", (short)slotA);
            del.Parameters.AddWithValue("pb", (short)pageB);
            del.Parameters.AddWithValue("sb", (short)slotB);
            await using var reader = await del.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                removed.Add(ReadEntry(reader));
        }

        var a = removed.FirstOrDefault(s => s.Page == pageA && s.Slot == slotA);
        var b = removed.FirstOrDefault(s => s.Page == pageB && s.Slot == slotB);

        if (b is not null) { b.Page = pageA; b.Slot = slotA; await UpsertAsync(conn, tx, b); }
        if (a is not null) { a.Page = pageB; a.Slot = slotB; await UpsertAsync(conn, tx, a); }

        await tx.CommitAsync();
    }

 /// <summary>
 /// Upsert keyed on (character, page, slot): an occupied position is replaced; the row id is assigned on
 /// first insert only.
 /// </summary>
    private static async Task UpsertAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CharacterQuickSlot e)
    {
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (id, character_id, page, slot, entry_type, entry_id, amount, item_uid) " +
            "VALUES (@id, @c, @p, @s, @t, @e, @a, @u) " +
            "ON CONFLICT (character_id, page, slot) DO UPDATE SET " +
            "entry_type = EXCLUDED.entry_type, entry_id = EXCLUDED.entry_id, " +
            "amount = EXCLUDED.amount, item_uid = EXCLUDED.item_uid",
            conn, tx);
        cmd.Parameters.AddWithValue("id", unchecked((long)(ulong)DateTime.UtcNow.Ticks));
        cmd.Parameters.AddWithValue("c", (long)e.CharacterId);
        cmd.Parameters.AddWithValue("p", (short)e.Page);
        cmd.Parameters.AddWithValue("s", (short)e.Slot);
        cmd.Parameters.AddWithValue("t", (short)e.EntryType);
        cmd.Parameters.AddWithValue("e", (long)e.EntryId);
        cmd.Parameters.AddWithValue("a", (int)e.Amount);
        cmd.Parameters.AddWithValue("u", unchecked((long)e.ItemUid));
        await cmd.ExecuteNonQueryAsync();
    }

    private static CharacterQuickSlot ReadEntry(NpgsqlDataReader r) => new()
    {
        Id = unchecked((ulong)r.GetInt64(0)),
        CharacterId = (uint)r.GetInt64(1),
        Page = (byte)r.GetInt16(2),
        Slot = (byte)r.GetInt16(3),
        EntryType = (byte)r.GetInt16(4),
        EntryId = (uint)r.GetInt64(5),
        Amount = (ushort)r.GetInt32(6),
        ItemUid = unchecked((ulong)r.GetInt64(7)),
    };
}
