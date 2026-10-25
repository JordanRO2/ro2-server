using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Characters.Domain;
using RO2.Persistence.Postgres;

namespace RO2.Characters.Infrastructure.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="ICharacterRepository"/>: the <c>characters</c> table, one row per
/// <see cref="Character"/> with a column per scalar field (no child table, no jsonb). Mirrors the Mongo provider
/// exactly except for id allocation: the id is <c>GENERATED ALWAYS AS IDENTITY</c> and
/// <see cref="CreateCharacterAsync"/> reads it back with <c>INSERT … RETURNING id</c> instead of Mongo's racy
/// <c>max(Id)+1</c>.
/// </summary>
/// <remarks>
/// Postgres has no unsigned integers: uint/ushort/ulong map to <c>bigint</c> (ulong bit-exact through
/// <c>unchecked</c> casts), byte to <c>smallint</c>, int to <c>integer</c>, float to <c>real</c>, bool to
/// <c>boolean</c>, and every DateTime is bound as UTC to <c>timestamptz</c>. <c>Character.Int</c> is column
/// <c>intel</c>. <c>appearance_json</c> is <c>text</c>, stored and returned verbatim (not jsonb). Each Mongo
/// UpdateOne becomes one <c>UPDATE … WHERE &lt;same predicate&gt; RETURNING id</c> where it reports a match; a
/// returned row is a matched document. The targeted single-field updates filter on the id only, like Mongo.
/// Case-insensitive name lookups use <c>lower(name)</c> in place of Mongo's case-insensitive regex / "en"
/// secondary-strength collation.
/// </remarks>
public sealed class CharacterRepository : ICharacterRepository
{
    private const int MaxCharactersPerAccount = 20; 
    private const int DefaultMapId = 1100; 
    private const float DefaultX = 769f;
    private const float DefaultY = 982f;
    private const float DefaultZ = 47f;

    private const string Columns =
        "id, account_id, name, job, race, life_job, gender, level, exp, job_level, job_exp, master_level, map_id, " +
        "x, y, z, hp, max_hp, mp, max_mp, str, agi, vit, intel, wis, luk, " +
        "bonus_str, bonus_agi, bonus_int, bonus_wis, bonus_vit, bonus_luk, gold, bank_gold, bind_index, " +
        "facing_yaw, appearance_json, equip_hide_flag, allow_anyone_review, bank_slots, created_at, last_played, " +
        "play_time_seconds, is_deleted, deleted_at";

 /// <summary>Every column but the identity id: the SET list of the full-row replace.</summary>
    private static readonly string[] ReplaceColumns =
    [
        "account_id", "name", "job", "race", "life_job", "gender", "level", "exp", "job_level", "job_exp",
        "master_level", "map_id", "x", "y", "z", "hp", "max_hp", "mp", "max_mp", "str", "agi", "vit", "intel",
        "wis", "luk", "bonus_str", "bonus_agi", "bonus_int", "bonus_wis", "bonus_vit", "bonus_luk", "gold",
        "bank_gold", "bind_index", "facing_yaw", "appearance_json", "equip_hide_flag", "allow_anyone_review",
        "bank_slots", "created_at", "last_played", "play_time_seconds", "is_deleted", "deleted_at",
    ];

    private static readonly string ReplaceSql =
        "UPDATE characters SET " + string.Join(", ", ReplaceColumns.Select(c => c + " = @" + c)) + " WHERE id = @id";

    private const string BulkSaveSql =
        "UPDATE characters SET map_id = @map_id, x = @x, y = @y, z = @z, hp = @hp, max_hp = @max_hp, mp = @mp, " +
        "max_mp = @max_mp, level = @level, exp = @exp, gold = @gold, bank_gold = @bank_gold, job = @job, " +
        "bind_index = @bind_index, facing_yaw = @facing_yaw, last_played = @now WHERE id = @id";

    private readonly PostgresDbContext _db;

    public CharacterRepository(PostgresDbContext db) => _db = db;

    private static DateTime Utc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };

    private static object UtcOrNull(DateTime? t) => t is { } v ? Utc(v) : DBNull.Value;

    private static void Add(NpgsqlParameterCollection p, string name, long value) => p.AddWithValue(name, NpgsqlDbType.Bigint, value);
    private static void Add(NpgsqlParameterCollection p, string name, ulong value) => p.AddWithValue(name, NpgsqlDbType.Bigint, unchecked((long)value));
    private static void Add(NpgsqlParameterCollection p, string name, int value) => p.AddWithValue(name, NpgsqlDbType.Integer, value);
    private static void Add(NpgsqlParameterCollection p, string name, byte value) => p.AddWithValue(name, NpgsqlDbType.Smallint, (short)value);
    private static void Add(NpgsqlParameterCollection p, string name, float value) => p.AddWithValue(name, NpgsqlDbType.Real, value);
    private static void Add(NpgsqlParameterCollection p, string name, bool value) => p.AddWithValue(name, NpgsqlDbType.Boolean, value);
    private static void Add(NpgsqlParameterCollection p, string name, DateTime value) => p.AddWithValue(name, NpgsqlDbType.TimestampTz, Utc(value));

    private static void AddText(NpgsqlParameterCollection p, string name, string? value) =>
        p.AddWithValue(name, NpgsqlDbType.Text, (object?)value ?? DBNull.Value);

    private static void AddTimestamp(NpgsqlParameterCollection p, string name, DateTime? value) =>
        p.AddWithValue(name, NpgsqlDbType.TimestampTz, UtcOrNull(value));

    private static void AddIds(NpgsqlCommand cmd, IReadOnlyCollection<uint> ids) =>
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
            Value = ids.Select(i => (long)i).ToArray(),
        });

    private static Character ReadCharacter(NpgsqlDataReader r) => new()
    {
        Id = (uint)r.GetInt64(0),
        AccountId = (uint)r.GetInt64(1),
        Name = r.GetString(2),
        Job = (byte)r.GetInt16(3),
        Race = (byte)r.GetInt16(4),
        LifeJob = (byte)r.GetInt16(5),
        Gender = (byte)r.GetInt16(6),
        Level = (byte)r.GetInt16(7),
        Exp = unchecked((ulong)r.GetInt64(8)),
        JobLevel = (byte)r.GetInt16(9),
        JobExp = unchecked((ulong)r.GetInt64(10)),
        MasterLevel = (ushort)r.GetInt64(11),
        MapId = (uint)r.GetInt64(12),
        X = r.GetFloat(13),
        Y = r.GetFloat(14),
        Z = r.GetFloat(15),
        Hp = r.GetInt32(16),
        MaxHp = r.GetInt32(17),
        Mp = r.GetInt32(18),
        MaxMp = r.GetInt32(19),
        Str = r.GetInt32(20),
        Agi = r.GetInt32(21),
        Vit = r.GetInt32(22),
        Int = r.GetInt32(23),
        Wis = r.GetInt32(24),
        Luk = r.GetInt32(25),
        BonusStr = r.GetInt32(26),
        BonusAgi = r.GetInt32(27),
        BonusInt = r.GetInt32(28),
        BonusWis = r.GetInt32(29),
        BonusVit = r.GetInt32(30),
        BonusLuk = r.GetInt32(31),
        Gold = r.GetInt64(32),
        BankGold = r.GetInt64(33),
        BindIndex = (ushort)r.GetInt64(34),
        FacingYaw = r.GetFloat(35),
        AppearanceJson = r.IsDBNull(36) ? null : r.GetString(36),
        EquipHideFlag = (ushort)r.GetInt64(37),
        AllowAnyoneReview = r.GetBoolean(38),
        BankSlots = (byte)r.GetInt16(39),
        CreatedAt = r.GetDateTime(40),
        LastPlayed = r.IsDBNull(41) ? null : r.GetDateTime(41),
        PlayTimeSeconds = r.GetInt64(42),
        IsDeleted = r.GetBoolean(43),
        DeletedAt = r.IsDBNull(44) ? null : r.GetDateTime(44),
    };

    private async Task<List<Character>> QueryAsync(string where, Action<NpgsqlCommand> bind, string suffix, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT " + Columns + " FROM characters WHERE " + where + suffix, conn);
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Character>();
        while (await reader.ReadAsync(ct)) list.Add(ReadCharacter(reader));
        return list;
    }

    private async Task<bool> AnyAsync(string where, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM characters WHERE " + where + " LIMIT 1", conn);
        bind(cmd);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

 /// <summary>Run an UPDATE that ends in <c>RETURNING id</c>; true when it matched a row.</summary>
    private async Task<bool> UpdateReturningAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task ExecAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountLiveAsync(NpgsqlConnection conn, uint accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM characters WHERE account_id = @a AND NOT is_deleted", conn);
        Add(cmd.Parameters, "a", (long)accountId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> LiveNameExistsAsync(NpgsqlConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM characters WHERE name = @n AND NOT is_deleted LIMIT 1", conn);
        AddText(cmd.Parameters, "n", name);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<Character?> GetByIdAsync(uint id)
    {
        var rows = await QueryAsync("id = @id AND NOT is_deleted", c => Add(c.Parameters, "id", (long)id), " LIMIT 1", CancellationToken.None);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<Character>> GetCharactersByAccountAsync(uint accountId)
        => await QueryAsync("account_id = @a AND NOT is_deleted", c => Add(c.Parameters, "a", (long)accountId),
            " ORDER BY created_at", CancellationToken.None);

    public Task<bool> CharacterNameExistsAsync(string name)
        => AnyAsync("name = @n AND NOT is_deleted", c => AddText(c.Parameters, "n", name), CancellationToken.None);

    public async Task<(Character? character, string? error)> CreateCharacterAsync(
        uint accountId, string name, byte job, byte gender, string appearanceJson,
        (int hp, int mp, int str, int agi, int intel, int wis, int vit, int luk)? stats = null)
    {
        await using var conn = await _db.OpenConnectionAsync();

        var count = await CountLiveAsync(conn, accountId, CancellationToken.None);
        if (count >= MaxCharactersPerAccount)
            return (null, "Maximum character limit reached");

        if (string.IsNullOrWhiteSpace(name) || name.Length < 2 || name.Length > 16)
            return (null, "Invalid character name length");

        if (await LiveNameExistsAsync(conn, name, CancellationToken.None))
            return (null, "Character name already exists");

        
        
        
        var st = stats ?? ((System.Func<(int, int, int, int, int, int, int, int)>)(() =>
        {
            var (h, m, s2, a2, v2, i2, d2, l2) = GetBaseStats(job);
            return (h, m, s2, a2, i2, d2, v2, l2);
        }))();

        var character = new Character
        {
            AccountId = accountId,
            Name = name,
            Job = job,
            Gender = gender,
            Level = 1,
            MapId = DefaultMapId,
            X = DefaultX,
            Y = DefaultY,
            Z = DefaultZ,
            Hp = st.hp,
            MaxHp = st.hp,
            Mp = st.mp,
            MaxMp = st.mp,
            Str = st.str,
            Agi = st.agi,
            Vit = st.vit,
            Int = st.intel,
            Wis = st.wis,
            Luk = st.luk,
            AppearanceJson = appearanceJson,
            CreatedAt = DateTime.UtcNow,
        };

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO characters (account_id, name, job, gender, level, map_id, x, y, z, hp, max_hp, mp, max_mp, " +
            "str, agi, vit, intel, wis, luk, appearance_json, created_at) " +
            "VALUES (@account_id, @name, @job, @gender, @level, @map_id, @x, @y, @z, @hp, @max_hp, @mp, @max_mp, " +
            "@str, @agi, @vit, @intel, @wis, @luk, @appearance_json, @created_at) RETURNING id", conn);
        var p = cmd.Parameters;
        Add(p, "account_id", (long)character.AccountId);
        AddText(p, "name", character.Name);
        Add(p, "job", character.Job);
        Add(p, "gender", character.Gender);
        Add(p, "level", character.Level);
        Add(p, "map_id", (long)character.MapId);
        Add(p, "x", character.X);
        Add(p, "y", character.Y);
        Add(p, "z", character.Z);
        Add(p, "hp", character.Hp);
        Add(p, "max_hp", character.MaxHp);
        Add(p, "mp", character.Mp);
        Add(p, "max_mp", character.MaxMp);
        Add(p, "str", character.Str);
        Add(p, "agi", character.Agi);
        Add(p, "vit", character.Vit);
        Add(p, "intel", character.Int);
        Add(p, "wis", character.Wis);
        Add(p, "luk", character.Luk);
        AddText(p, "appearance_json", character.AppearanceJson);
        Add(p, "created_at", character.CreatedAt);
        character.Id = (uint)Convert.ToInt64(await cmd.ExecuteScalarAsync());
        return (character, null);
    }

    public Task<bool> DeleteCharacterAsync(uint characterId, uint accountId)
        => UpdateReturningAsync(
            "UPDATE characters SET is_deleted = true, deleted_at = @now " +
            "WHERE id = @id AND account_id = @a AND NOT is_deleted RETURNING id",
            c =>
            {
                Add(c.Parameters, "now", DateTime.UtcNow);
                Add(c.Parameters, "id", (long)characterId);
                Add(c.Parameters, "a", (long)accountId);
            }, CancellationToken.None);

    public async Task<CharacterRestoreResult> TryRestoreCharacterAsync(uint characterId, uint accountId, DateTime notDeletedBeforeUtc)
    {
        var ct = CancellationToken.None;
        await using var conn = await _db.OpenConnectionAsync(ct);

        
        string name;
        bool isDeleted;
        DateTime? deletedAt;
        await using (var sel = new NpgsqlCommand(
            "SELECT name, is_deleted, deleted_at FROM characters WHERE id = @id AND account_id = @a LIMIT 1", conn))
        {
            Add(sel.Parameters, "id", (long)characterId);
            Add(sel.Parameters, "a", (long)accountId);
            await using var r = await sel.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return CharacterRestoreResult.NotFound;
            name = r.GetString(0);
            isDeleted = r.GetBoolean(1);
            deletedAt = r.IsDBNull(2) ? null : r.GetDateTime(2);
        }

        if (!isDeleted) return CharacterRestoreResult.AlreadyLive;
        var cutoff = Utc(notDeletedBeforeUtc);
        if (deletedAt is not { } at || at < cutoff)
            return CharacterRestoreResult.Expired;

        
        
        if (await LiveNameExistsAsync(conn, name, ct)) return CharacterRestoreResult.NameTaken;
        if (await CountLiveAsync(conn, accountId, ct) >= MaxCharactersPerAccount) return CharacterRestoreResult.AccountFull;

        await using (var upd = new NpgsqlCommand(
            "UPDATE characters SET is_deleted = false, deleted_at = NULL " +
            "WHERE id = @id AND account_id = @a AND is_deleted = true AND deleted_at >= @cutoff RETURNING id", conn))
        {
            Add(upd.Parameters, "id", (long)characterId);
            Add(upd.Parameters, "a", (long)accountId);
            Add(upd.Parameters, "cutoff", cutoff);
            if (await upd.ExecuteScalarAsync(ct) is not null) return CharacterRestoreResult.Restored;
        }

        
        
        await using var again = new NpgsqlCommand(
            "SELECT 1 FROM characters WHERE id = @id AND account_id = @a AND NOT is_deleted LIMIT 1", conn);
        Add(again.Parameters, "id", (long)characterId);
        Add(again.Parameters, "a", (long)accountId);
        return await again.ExecuteScalarAsync(ct) is not null ? CharacterRestoreResult.AlreadyLive : CharacterRestoreResult.NotFound;
    }

    public Task SaveCharacterAsync(Character character)
    {
        character.LastPlayed = DateTime.UtcNow;
        return ExecAsync(ReplaceSql, c =>
        {
            var p = c.Parameters;
            Add(p, "id", (long)character.Id);
            Add(p, "account_id", (long)character.AccountId);
            AddText(p, "name", character.Name);
            Add(p, "job", character.Job);
            Add(p, "race", character.Race);
            Add(p, "life_job", character.LifeJob);
            Add(p, "gender", character.Gender);
            Add(p, "level", character.Level);
            Add(p, "exp", character.Exp);
            Add(p, "job_level", character.JobLevel);
            Add(p, "job_exp", character.JobExp);
            Add(p, "master_level", (long)character.MasterLevel);
            Add(p, "map_id", (long)character.MapId);
            Add(p, "x", character.X);
            Add(p, "y", character.Y);
            Add(p, "z", character.Z);
            Add(p, "hp", character.Hp);
            Add(p, "max_hp", character.MaxHp);
            Add(p, "mp", character.Mp);
            Add(p, "max_mp", character.MaxMp);
            Add(p, "str", character.Str);
            Add(p, "agi", character.Agi);
            Add(p, "vit", character.Vit);
            Add(p, "intel", character.Int);
            Add(p, "wis", character.Wis);
            Add(p, "luk", character.Luk);
            Add(p, "bonus_str", character.BonusStr);
            Add(p, "bonus_agi", character.BonusAgi);
            Add(p, "bonus_int", character.BonusInt);
            Add(p, "bonus_wis", character.BonusWis);
            Add(p, "bonus_vit", character.BonusVit);
            Add(p, "bonus_luk", character.BonusLuk);
            Add(p, "gold", character.Gold);
            Add(p, "bank_gold", character.BankGold);
            Add(p, "bind_index", (long)character.BindIndex);
            Add(p, "facing_yaw", character.FacingYaw);
            AddText(p, "appearance_json", character.AppearanceJson);
            Add(p, "equip_hide_flag", (long)character.EquipHideFlag);
            Add(p, "allow_anyone_review", character.AllowAnyoneReview);
            Add(p, "bank_slots", character.BankSlots);
            Add(p, "created_at", character.CreatedAt);
            AddTimestamp(p, "last_played", character.LastPlayed);
            Add(p, "play_time_seconds", character.PlayTimeSeconds);
            Add(p, "is_deleted", character.IsDeleted);
            AddTimestamp(p, "deleted_at", character.DeletedAt);
        }, CancellationToken.None);
    }

    public Task UpdatePositionAsync(uint characterId, uint mapId, float x, float y, float z)
        => ExecAsync(
            "UPDATE characters SET map_id = @map_id, x = @x, y = @y, z = @z, last_played = @now WHERE id = @id",
            c =>
            {
                Add(c.Parameters, "map_id", (long)mapId);
                Add(c.Parameters, "x", x);
                Add(c.Parameters, "y", y);
                Add(c.Parameters, "z", z);
                Add(c.Parameters, "now", DateTime.UtcNow);
                Add(c.Parameters, "id", (long)characterId);
            }, CancellationToken.None);

    public Task UpdateStatsAsync(uint characterId, int hp, int mp, byte level, ulong exp)
        => ExecAsync(
            "UPDATE characters SET hp = @hp, mp = @mp, level = @level, exp = @exp, last_played = @now WHERE id = @id",
            c =>
            {
                Add(c.Parameters, "hp", hp);
                Add(c.Parameters, "mp", mp);
                Add(c.Parameters, "level", level);
                Add(c.Parameters, "exp", exp);
                Add(c.Parameters, "now", DateTime.UtcNow);
                Add(c.Parameters, "id", (long)characterId);
            }, CancellationToken.None);

    public Task UpdateGoldAsync(uint characterId, long gold)
        => ExecAsync(
            "UPDATE characters SET gold = @gold, last_played = @now WHERE id = @id",
            c =>
            {
                Add(c.Parameters, "gold", gold);
                Add(c.Parameters, "now", DateTime.UtcNow);
                Add(c.Parameters, "id", (long)characterId);
            }, CancellationToken.None);

    public Task UpdateEquipHideFlagAsync(uint characterId, ushort hideFlag)
        => ExecAsync(
            "UPDATE characters SET equip_hide_flag = @flag WHERE id = @id",
            c =>
            {
                Add(c.Parameters, "flag", (long)hideFlag);
                Add(c.Parameters, "id", (long)characterId);
            }, CancellationToken.None);

    public async Task BulkSavePlayerStateAsync(IReadOnlyList<PlayerStateSave> saves)
    {
        if (saves.Count == 0) return;
        var now = DateTime.UtcNow;
        await using var conn = await _db.OpenConnectionAsync();
        await using var batch = new NpgsqlBatch(conn);
        foreach (var s in saves)
        {
            var cmd = new NpgsqlBatchCommand(BulkSaveSql);
            var p = cmd.Parameters;
            Add(p, "map_id", (long)s.MapId);
            Add(p, "x", s.X);
            Add(p, "y", s.Y);
            Add(p, "z", s.Z);
            Add(p, "hp", s.Hp);
            Add(p, "max_hp", s.MaxHp);
            Add(p, "mp", s.Mp);
            Add(p, "max_mp", s.MaxMp);
            Add(p, "level", s.Level);
            Add(p, "exp", s.Exp);
            Add(p, "gold", s.Gold);
            Add(p, "bank_gold", s.BankGold);
            Add(p, "job", s.Job);
            Add(p, "bind_index", (long)s.BindIndex);
            Add(p, "facing_yaw", s.FacingYaw);
            Add(p, "now", now);
            Add(p, "id", (long)s.CharacterId);
            batch.BatchCommands.Add(cmd);
        }
        await batch.ExecuteNonQueryAsync();
    }

    public Task UpdateAllowAnyoneReviewAsync(uint characterId, bool allow, CancellationToken ct = default)
        => ExecAsync(
            "UPDATE characters SET allow_anyone_review = @allow WHERE id = @id",
            c =>
            {
                Add(c.Parameters, "allow", allow);
                Add(c.Parameters, "id", (long)characterId);
            }, ct);

    public Task<bool> UpdateAppearanceAsync(uint characterId, string appearanceJson, byte gender, byte race, CancellationToken ct = default)
        => UpdateReturningAsync(
            "UPDATE characters SET appearance_json = @json, gender = @gender, race = @race " +
            "WHERE id = @id AND NOT is_deleted RETURNING id",
            c =>
            {
                AddText(c.Parameters, "json", appearanceJson);
                Add(c.Parameters, "gender", gender);
                Add(c.Parameters, "race", race);
                Add(c.Parameters, "id", (long)characterId);
            }, ct);

    public Task UpdateAppearanceAndJobAsync(uint characterId, string? appearanceJson, byte? job, CancellationToken ct = default)
        => ExecAsync(
            job is null
                ? "UPDATE characters SET appearance_json = @json WHERE id = @id"
                : "UPDATE characters SET appearance_json = @json, job = @job WHERE id = @id",
            c =>
            {
                AddText(c.Parameters, "json", appearanceJson);
                if (job is { } j) Add(c.Parameters, "job", j);
                Add(c.Parameters, "id", (long)characterId);
            }, ct);

    public async Task<Character?> FindByNameIgnoreCaseAsync(string name, CancellationToken ct = default)
    {
        var rows = await QueryAsync("lower(name) = lower(@n) AND NOT is_deleted", c => AddText(c.Parameters, "n", name), " LIMIT 1", ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<Character?> FindByExactNameAsync(string name, CancellationToken ct = default)
    {
        var rows = await QueryAsync("name = @n AND NOT is_deleted", c => AddText(c.Parameters, "n", name), " LIMIT 1", ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public Task<bool> AccountHasLiveCharacterAsync(uint accountId, CancellationToken ct = default)
        => AnyAsync("account_id = @a AND NOT is_deleted", c => Add(c.Parameters, "a", (long)accountId), ct);

    public Task<bool> NameExistsIgnoreCaseAsync(string name, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(name)
            ? Task.FromResult(false)
            : AnyAsync("lower(name) = lower(@n) AND NOT is_deleted", c => AddText(c.Parameters, "n", name), ct);

    public Task<bool> RenameAsync(uint characterId, string oldName, string newName, CancellationToken ct = default)
        => UpdateReturningAsync(
            "UPDATE characters SET name = @new WHERE id = @id AND name = @old AND NOT is_deleted RETURNING id",
            c =>
            {
                AddText(c.Parameters, "new", newName);
                Add(c.Parameters, "id", (long)characterId);
                AddText(c.Parameters, "old", oldName);
            }, ct);

    public Task SetBankSlotsAsync(uint characterId, byte slots, CancellationToken ct = default)
        => ExecAsync(
            "UPDATE characters SET bank_slots = @slots WHERE id = @id",
            c =>
            {
                Add(c.Parameters, "slots", slots);
                Add(c.Parameters, "id", (long)characterId);
            }, ct);

    public async Task<IReadOnlyList<Character>> GetByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default)
        => await QueryAsync("id = ANY(@ids)", c => AddIds(c, ids), string.Empty, ct);

    public async Task<int> CountLiveByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM characters WHERE id = ANY(@ids) AND NOT is_deleted", conn);
        AddIds(cmd, ids);
        return (int)Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<Character>> GetLiveByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default)
        => await QueryAsync("id = ANY(@ids) AND NOT is_deleted", c => AddIds(c, ids), string.Empty, ct);

    public async Task<IReadOnlyList<Character>> FindByNamesCollatedAsync(IReadOnlyCollection<string> names, CancellationToken ct = default)
        => await QueryAsync(
            "lower(name) = ANY(SELECT lower(x) FROM unnest(@names) AS x) AND NOT is_deleted",
            c => c.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names.ToArray() }),
            string.Empty, ct);

    private static (int hp, int mp, int str, int agi, int vit, int @int, int dex, int luk) GetBaseStats(byte job)
    {
        return job switch
        {
            0 => (100, 50, 10, 10, 10, 10, 10, 10),
            1 => (120, 40, 12, 10, 12, 8, 10, 8),
            2 => (80, 80, 8, 12, 8, 12, 10, 10),
            3 => (90, 60, 8, 14, 8, 10, 14, 6),
            4 => (100, 60, 10, 10, 10, 10, 10, 10),
            5 => (85, 50, 10, 14, 8, 8, 12, 8),
            _ => (100, 50, 10, 10, 10, 10, 10, 10)
        };
    }
}
