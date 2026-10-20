using Npgsql;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="ICraftRecipeRepository"/>: each character's explicitly learned
/// crafting recipes in the <c>craft_recipes</c> table, one row per (character, recipe) pair. The Mongo
/// provider kept one document per character with a <c>Recipes</c> set; here the set is the table itself,
/// with <c>PRIMARY KEY (character_id, recipe_id)</c> standing in for <c>$addToSet</c> uniqueness.
/// </summary>
/// <remarks>
/// <para>
/// Learning is a single <c>INSERT … ON CONFLICT DO NOTHING</c>: the primary key makes "already learned" an
/// atomic database decision, so a repeated or concurrent 1348 for the same recipe has exactly one winner
/// (one affected row, <c>true</c>) and every other caller affects zero rows (<c>false</c>), with no
/// read-then-write window. Reads return the recipes in the order they were learned (an identity
/// <c>seq</c> column), matching the append order of the Mongo <c>$addToSet</c> array.
/// </para>
/// <para>
/// Postgres has no unsigned integers, so the <c>uint</c> character id and the <c>ushort</c> recipe id are
/// both stored as <c>bigint</c> and cast back on read.
/// </para>
/// </remarks>
public sealed class CraftRecipeRepository : ICraftRecipeRepository
{
    public const string TableName = "craft_recipes";

    private readonly PostgresDbContext _db;

    public CraftRecipeRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<ushort>> GetAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT recipe_id FROM {TableName} WHERE character_id = @c ORDER BY seq", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var recipes = new List<ushort>();
        while (await reader.ReadAsync(ct))
            recipes.Add((ushort)reader.GetInt64(0));
        return recipes;
    }

    public async Task<bool> AddAsync(uint characterId, ushort recipeId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, recipe_id) VALUES (@c, @r) " +
            "ON CONFLICT (character_id, recipe_id) DO NOTHING", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("r", (long)recipeId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
