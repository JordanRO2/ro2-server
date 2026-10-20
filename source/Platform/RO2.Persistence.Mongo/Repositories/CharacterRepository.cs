using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

public class CharacterRepository : ICharacterRepository
{
    /// <summary>
    /// Case-insensitive name match of <see cref="FindByNamesCollatedAsync"/>. The typed block-list name is resolved
    /// this way and answered with the stored spelling: a 23006 for "iro2chatbot" is answered by a 23510
    /// carrying "iRO2ChatBot".
    /// </summary>
    private static readonly Collation NameCollation = new("en", strength: CollationStrength.Secondary);

    private readonly IMongoCollection<Character> _characters;

    /// <summary>Name of the case-insensitive index on <c>Name</c> (see <see cref="EnsureIndexes"/>).</summary>
    private const string NameIndexName = "Name_ci";

    /// <summary>Live (not deleted) characters one account may hold; above the retail slot count so a dev account can keep one test character per map.</summary>
    private const int MaxCharactersPerAccount = 20;

    /// <summary>Shortest accepted character name, in UTF-16 code units.</summary>
    private const int MinNameLength = 2;

    /// <summary>Longest accepted character name, in UTF-16 code units.</summary>
    private const int MaxNameLength = 16;

    /// <summary>Level every new character starts at.</summary>
    private const byte StartingLevel = 1;

    // New-character spawn: map 1100 (Prontera City) at world position (769, 982, 47).
    private const int DefaultMapId = 1100;
    private const float DefaultX = 769f;
    private const float DefaultY = 982f;
    private const float DefaultZ = 47f;

    public CharacterRepository(MongoDbContext context)
    {
        _characters = context.Characters;
        EnsureIndexes();
    }

    private static int _indexesEnsured; // 0 = not yet

    /// <summary>
    /// Case-insensitive (same collation as <see cref="FindByNamesCollatedAsync"/>) index on Name, so the chat
    /// friend/block name lookup is a seek. Non-unique, so pre-existing duplicate names cannot fail the build.
    /// Built once per process, off the caller's path; any failure (Mongo down, conflicting same-key index) is
    /// swallowed and retried by the next repository instance.
    /// </summary>
    private void EnsureIndexes()
    {
        if (Interlocked.Exchange(ref _indexesEnsured, 1) != 0) return;
        var characters = _characters;
        _ = Task.Run(async () =>
        {
            try
            {
                await characters.Indexes.CreateOneAsync(new CreateIndexModel<Character>(
                    Builders<Character>.IndexKeys.Ascending(c => c.Name),
                    new CreateIndexOptions { Name = NameIndexName, Collation = NameCollation }));
            }
            catch
            {
                Interlocked.Exchange(ref _indexesEnsured, 0);
            }
        });
    }

    public async Task<Character?> GetByIdAsync(uint id)
    {
        return await _characters.Find(c => c.Id == id && !c.IsDeleted).FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<Character>> GetCharactersByAccountAsync(uint accountId)
    {
        return await _characters
            .Find(c => c.AccountId == accountId && !c.IsDeleted)
            .SortBy(c => c.CreatedAt)
            .ToListAsync();
    }

    public async Task<bool> CharacterNameExistsAsync(string name)
    {
        return await _characters.Find(c => c.Name == name && !c.IsDeleted).AnyAsync();
    }

    public async Task<(Character? character, string? error)> CreateCharacterAsync(
        uint accountId, string name, byte job, byte gender, string appearanceJson,
        (int hp, int mp, int str, int agi, int intel, int wis, int vit, int luk)? stats = null)
    {
        var count = await _characters.CountDocumentsAsync(c => c.AccountId == accountId && !c.IsDeleted);
        if (count >= MaxCharactersPerAccount)
            return (null, "Maximum character limit reached");

        if (string.IsNullOrWhiteSpace(name) || name.Length < MinNameLength || name.Length > MaxNameLength)
            return (null, "Invalid character name length");

        if (await CharacterNameExistsAsync(name))
            return (null, "Character name already exists");

        var maxId = await _characters.Find(_ => true)
            .SortByDescending(c => c.Id)
            .Limit(1)
            .FirstOrDefaultAsync();

        var nextId = (maxId?.Id ?? 0) + 1;

        // Real, caller-computed class stats (from Standard_Stat + the PCBaseStatus HP/SP formula) when
        // supplied; otherwise fall back to the legacy hardcoded table (order hp,mp,str,agi,vit,int,dex,luk
        // → remapped to the new hp,mp,str,agi,intel,wis(=dex),vit,luk shape).
        var st = stats ?? ((System.Func<(int, int, int, int, int, int, int, int)>)(() =>
        {
            var (h, m, s2, a2, v2, i2, d2, l2) = GetBaseStats(job);
            return (h, m, s2, a2, i2, d2, v2, l2);
        }))();

        var character = new Character
        {
            Id = nextId,
            AccountId = accountId,
            Name = name,
            Job = job,
            Gender = gender,
            Level = StartingLevel,
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
            CreatedAt = DateTime.UtcNow
        };

        await _characters.InsertOneAsync(character);
        return (character, null);
    }

    public async Task<bool> DeleteCharacterAsync(uint characterId, uint accountId)
    {
        var update = Builders<Character>.Update
            .Set(c => c.IsDeleted, true)
            .Set(c => c.DeletedAt, DateTime.UtcNow);

        var result = await _characters.UpdateOneAsync(
            c => c.Id == characterId && c.AccountId == accountId && !c.IsDeleted,
            update);

        return result.ModifiedCount > 0;
    }

    public async Task<CharacterRestoreResult> TryRestoreCharacterAsync(uint characterId, uint accountId, DateTime notDeletedBeforeUtc)
    {
        // Every document of this account with that id, deleted or not (GetByIdAsync sees only live ones).
        var character = await _characters
            .Find(c => c.Id == characterId && c.AccountId == accountId)
            .FirstOrDefaultAsync();
        if (character == null) return CharacterRestoreResult.NotFound;
        if (!character.IsDeleted) return CharacterRestoreResult.AlreadyLive;
        if (character.DeletedAt is not { } deletedAt || deletedAt < notDeletedBeforeUtc)
            return CharacterRestoreResult.Expired;

        // CharacterNameExistsAsync and the create limit count only live characters, so while this one
        // was deleted its name may have gone to a new character, and its slot may have been filled.
        // Same check-then-write order as CreateCharacterAsync.
        if (await CharacterNameExistsAsync(character.Name)) return CharacterRestoreResult.NameTaken;
        var live = await _characters.CountDocumentsAsync(c => c.AccountId == accountId && !c.IsDeleted);
        if (live >= MaxCharactersPerAccount) return CharacterRestoreResult.AccountFull;

        var filter = Builders<Character>.Filter.Eq(c => c.Id, characterId)
            & Builders<Character>.Filter.Eq(c => c.AccountId, accountId)
            & Builders<Character>.Filter.Eq(c => c.IsDeleted, true)
            & Builders<Character>.Filter.Gte(c => c.DeletedAt, (DateTime?)notDeletedBeforeUtc);
        var update = Builders<Character>.Update
            .Set(c => c.IsDeleted, false)
            .Set(c => c.DeletedAt, (DateTime?)null);
        var result = await _characters.UpdateOneAsync(filter, update);
        if (result.ModifiedCount > 0) return CharacterRestoreResult.Restored;

        // The document stopped matching between the read and the write. Another restore of the same
        // character won the race (it is live now), or the document changed some other way.
        var liveNow = await _characters
            .Find(c => c.Id == characterId && c.AccountId == accountId && !c.IsDeleted)
            .AnyAsync();
        return liveNow ? CharacterRestoreResult.AlreadyLive : CharacterRestoreResult.NotFound;
    }

    public async Task SaveCharacterAsync(Character character)
    {
        character.LastPlayed = DateTime.UtcNow;
        await _characters.ReplaceOneAsync(c => c.Id == character.Id, character);
    }

    public async Task UpdatePositionAsync(uint characterId, uint mapId, float x, float y, float z)
    {
        var update = Builders<Character>.Update
            .Set(c => c.MapId, mapId)
            .Set(c => c.X, x)
            .Set(c => c.Y, y)
            .Set(c => c.Z, z)
            .Set(c => c.LastPlayed, DateTime.UtcNow);
        await _characters.UpdateOneAsync(c => c.Id == characterId, update);
    }

    public async Task UpdateStatsAsync(uint characterId, int hp, int mp, byte level, ulong exp)
    {
        var update = Builders<Character>.Update
            .Set(c => c.Hp, hp)
            .Set(c => c.Mp, mp)
            .Set(c => c.Level, level)
            .Set(c => c.Exp, exp)
            .Set(c => c.LastPlayed, DateTime.UtcNow);
        await _characters.UpdateOneAsync(c => c.Id == characterId, update);
    }

    public async Task UpdateGoldAsync(uint characterId, long gold)
    {
        var update = Builders<Character>.Update
            .Set(c => c.Gold, gold)
            .Set(c => c.LastPlayed, DateTime.UtcNow);
        await _characters.UpdateOneAsync(c => c.Id == characterId, update);
    }

    public async Task UpdateEquipHideFlagAsync(uint characterId, ushort hideFlag)
    {
        var update = Builders<Character>.Update.Set(c => c.EquipHideFlag, hideFlag);
        await _characters.UpdateOneAsync(c => c.Id == characterId, update);
    }

    public async Task BulkSavePlayerStateAsync(IReadOnlyList<PlayerStateSave> saves)
    {
        if (saves.Count == 0) return;
        var now = DateTime.UtcNow;
        var ops = new List<WriteModel<Character>>(saves.Count);
        foreach (var s in saves)
        {
            var update = Builders<Character>.Update
                .Set(c => c.MapId, s.MapId).Set(c => c.X, s.X).Set(c => c.Y, s.Y).Set(c => c.Z, s.Z)
                .Set(c => c.Hp, s.Hp).Set(c => c.MaxHp, s.MaxHp).Set(c => c.Mp, s.Mp).Set(c => c.MaxMp, s.MaxMp)
                .Set(c => c.Level, s.Level).Set(c => c.Exp, s.Exp).Set(c => c.Gold, s.Gold)
                .Set(c => c.BankGold, s.BankGold)
                .Set(c => c.Job, s.Job).Set(c => c.BindIndex, s.BindIndex).Set(c => c.FacingYaw, s.FacingYaw)
                .Set(c => c.LastPlayed, now);
            ops.Add(new UpdateOneModel<Character>(
                Builders<Character>.Filter.Eq(c => c.Id, s.CharacterId), update));
        }
        // Unordered so the server applies the updates in parallel and one failing row doesn't block the rest.
        await _characters.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false });
    }

    public Task UpdateAllowAnyoneReviewAsync(uint characterId, bool allow, CancellationToken ct = default)
        => _characters.UpdateOneAsync(
            c => c.Id == characterId,
            Builders<Character>.Update.Set(c => c.AllowAnyoneReview, allow),
            cancellationToken: ct);

    public async Task<bool> UpdateAppearanceAsync(uint characterId, string appearanceJson, byte gender, byte race, CancellationToken ct = default)
    {
        var update = await _characters.UpdateOneAsync(
            c => c.Id == characterId && !c.IsDeleted,
            Builders<Character>.Update
                .Set(c => c.AppearanceJson, appearanceJson)
                .Set(c => c.Gender, gender)
                .Set(c => c.Race, race),
            cancellationToken: ct);
        return update.MatchedCount > 0;
    }

    public async Task UpdateAppearanceAndJobAsync(uint characterId, string? appearanceJson, byte? job, CancellationToken ct = default)
    {
        var update = Builders<Character>.Update.Set(x => x.AppearanceJson, appearanceJson);
        if (job is { } j) update = update.Set(x => x.Job, j);
        await _characters.UpdateOneAsync(x => x.Id == characterId, update, cancellationToken: ct);
    }

    public async Task<Character?> FindByNameIgnoreCaseAsync(string name, CancellationToken ct = default)
    {
        var filter = Builders<Character>.Filter.Regex(c => c.Name,
                         new BsonRegularExpression("^" + Regex.Escape(name) + "$", "i"))
                     & Builders<Character>.Filter.Eq(c => c.IsDeleted, false);
        return await _characters.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<Character?> FindByExactNameAsync(string name, CancellationToken ct = default)
        => await _characters.Find(c => c.Name == name && !c.IsDeleted).FirstOrDefaultAsync(ct);

    public async Task<bool> AccountHasLiveCharacterAsync(uint accountId, CancellationToken ct = default)
        => await _characters.Find(c => c.AccountId == accountId && !c.IsDeleted).AnyAsync(ct);

    public async Task<bool> NameExistsIgnoreCaseAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (await _characters.Find(c => c.Name == name && !c.IsDeleted).AnyAsync(ct)) return true;
        var rx = new BsonRegularExpression("^" + Regex.Escape(name) + "$", "i");
        var filter = Builders<Character>.Filter.Regex(c => c.Name, rx)
                     & Builders<Character>.Filter.Eq(c => c.IsDeleted, false);
        return await _characters.Find(filter).AnyAsync(ct);
    }

    public async Task<bool> RenameAsync(uint characterId, string oldName, string newName, CancellationToken ct = default)
    {
        var update = await _characters.UpdateOneAsync(
            c => c.Id == characterId && c.Name == oldName && !c.IsDeleted,
            Builders<Character>.Update.Set(c => c.Name, newName),
            cancellationToken: ct);
        return update.ModifiedCount > 0;
    }

    public Task SetBankSlotsAsync(uint characterId, byte slots, CancellationToken ct = default)
        => _characters.UpdateOneAsync(
            c => c.Id == characterId,
            Builders<Character>.Update.Set(c => c.BankSlots, slots),
            cancellationToken: ct);

    public async Task<IReadOnlyList<Character>> GetByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default)
        => await _characters.Find(Builders<Character>.Filter.In(c => c.Id, ids)).ToListAsync(ct);

    public async Task<int> CountLiveByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default)
    {
        var filter = Builders<Character>.Filter.In(c => c.Id, ids)
                     & Builders<Character>.Filter.Eq(c => c.IsDeleted, false);
        return (int)await _characters.CountDocumentsAsync(filter, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<Character>> GetLiveByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default)
    {
        var filter = Builders<Character>.Filter.In(c => c.Id, ids)
                     & Builders<Character>.Filter.Eq(c => c.IsDeleted, false);
        return await _characters.Find(filter).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Character>> FindByNamesCollatedAsync(IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var filter = Builders<Character>.Filter.In(c => c.Name, names)
                     & Builders<Character>.Filter.Eq(c => c.IsDeleted, false);
        return await _characters.Find(filter, new FindOptions { Collation = NameCollation }).ToListAsync(ct);
    }

    /// <summary>
    /// Fallback starting stats by job id, used only when the caller supplies none. The tuple order is
    /// (hp, mp, str, agi, vit, int, dex, luk); unlisted jobs get the job-0 row.
    /// </summary>
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
