using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Tests.Economy;

/// <summary>
/// In-memory <see cref="ICharacterRepository"/> for <see cref="EconomyFixture"/>. The wallet paths (read, UpdateGold,
/// SaveCharacter, the bulk autosave) and the id / name lookups behave like the real store; what the economy never
/// touches (creation, restore, appearance, rename) throws <see cref="NotSupportedException"/> so a cluster test that
/// needs it fails loudly instead of passing on a no-op. Rows are copied in and out.
/// </summary>
/// <remarks>The persisted <see cref="Character.Gold"/> is only the DB copy: while a player is online the live
/// <c>PlayerState.Gold</c> is the authoritative wallet (see <see cref="EconomyFixture.WalletOf"/>).</remarks>
public sealed class FakeCharacterRepository(RepoScheduler scheduler) : ICharacterRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, Character> _rows = [];

    /// <summary>Copies of every stored row; takes no scheduler point.</summary>
    public IReadOnlyList<Character> Rows { get { lock (_gate) return [.. _rows.Values.Select(EntityCopy.Clone)]; } }

    /// <summary>Test setup: stores a copy of the row without a scheduler point.</summary>
    public void Seed(Character character) { lock (_gate) _rows[character.Id] = EntityCopy.Clone(character); }

    private static NotSupportedException NotModelled(string what) =>
        new($"{what} is not modelled by the economy fixture's FakeCharacterRepository.");

    public async Task<Character?> GetByIdAsync(uint id)
    {
        await scheduler.PointAsync("Character.GetById");
        lock (_gate) return _rows.TryGetValue(id, out var c) ? EntityCopy.Clone(c) : null;
    }

    public async Task<IReadOnlyList<Character>> GetCharactersByAccountAsync(uint accountId)
    {
        await scheduler.PointAsync("Character.GetByAccount");
        lock (_gate) return [.. _rows.Values.Where(c => c.AccountId == accountId && !c.IsDeleted).Select(EntityCopy.Clone)];
    }

    public async Task<bool> CharacterNameExistsAsync(string name)
    {
        await scheduler.PointAsync("Character.NameExists");
        lock (_gate) return _rows.Values.Any(c => !c.IsDeleted && c.Name == name);
    }

    public Task<(Character? character, string? error)> CreateCharacterAsync(
        uint accountId, string name, byte job, byte gender, string appearanceJson,
        (int hp, int mp, int str, int agi, int intel, int wis, int vit, int luk)? stats = null)
        => throw NotModelled(nameof(CreateCharacterAsync));

    public Task<bool> DeleteCharacterAsync(uint characterId, uint accountId) => throw NotModelled(nameof(DeleteCharacterAsync));

    public Task<CharacterRestoreResult> TryRestoreCharacterAsync(uint characterId, uint accountId, DateTime notDeletedBeforeUtc)
        => throw NotModelled(nameof(TryRestoreCharacterAsync));

    /// <summary>Whole-row replace (as the real store does), so a stale snapshot overwrites the live fields.</summary>
    public async Task SaveCharacterAsync(Character character)
    {
        await scheduler.PointAsync("Character.Save");
        lock (_gate) _rows[character.Id] = EntityCopy.Clone(character);
    }

    public Task UpdatePositionAsync(uint characterId, uint mapId, float x, float y, float z)
        => throw NotModelled(nameof(UpdatePositionAsync));

    public Task UpdateStatsAsync(uint characterId, int hp, int mp, byte level, ulong exp)
        => throw NotModelled(nameof(UpdateStatsAsync));

    public async Task UpdateGoldAsync(uint characterId, long gold)
    {
        await scheduler.PointAsync("Character.UpdateGold");
        lock (_gate) { if (_rows.TryGetValue(characterId, out var c)) c.Gold = gold; }
    }

    public Task UpdateEquipHideFlagAsync(uint characterId, ushort hideFlag) => throw NotModelled(nameof(UpdateEquipHideFlagAsync));

    public async Task BulkSavePlayerStateAsync(IReadOnlyList<PlayerStateSave> saves)
    {
        await scheduler.PointAsync("Character.BulkSave");
        lock (_gate)
            foreach (var s in saves)
                if (_rows.TryGetValue(s.CharacterId, out var c))
                {
                    c.Gold = s.Gold;
                    c.BankGold = s.BankGold;
                    c.Hp = s.Hp; c.MaxHp = s.MaxHp; c.Mp = s.Mp; c.MaxMp = s.MaxMp;
                    c.Level = s.Level; c.Exp = s.Exp; c.Job = s.Job; c.MapId = s.MapId;
                    c.X = s.X; c.Y = s.Y; c.Z = s.Z; c.BindIndex = s.BindIndex; c.FacingYaw = s.FacingYaw;
                }
    }

    public Task UpdateAllowAnyoneReviewAsync(uint characterId, bool allow, CancellationToken ct = default)
        => throw NotModelled(nameof(UpdateAllowAnyoneReviewAsync));

    public Task<bool> UpdateAppearanceAsync(uint characterId, string appearanceJson, byte gender, byte race, CancellationToken ct = default)
        => throw NotModelled(nameof(UpdateAppearanceAsync));

    public Task UpdateAppearanceAndJobAsync(uint characterId, string? appearanceJson, byte? job, CancellationToken ct = default)
        => throw NotModelled(nameof(UpdateAppearanceAndJobAsync));

    public async Task<Character?> FindByNameIgnoreCaseAsync(string name, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Character.FindByNameIgnoreCase");
        lock (_gate)
            return _rows.Values.Where(c => !c.IsDeleted && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(EntityCopy.Clone).FirstOrDefault();
    }

    public async Task<Character?> FindByExactNameAsync(string name, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Character.FindByExactName");
        lock (_gate)
            return _rows.Values.Where(c => !c.IsDeleted && c.Name == name).Select(EntityCopy.Clone).FirstOrDefault();
    }

    public async Task<bool> AccountHasLiveCharacterAsync(uint accountId, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Character.AccountHasLive");
        lock (_gate) return _rows.Values.Any(c => c.AccountId == accountId && !c.IsDeleted);
    }

    public async Task<bool> NameExistsIgnoreCaseAsync(string name, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Character.NameExistsIgnoreCase");
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_gate)
            return _rows.Values.Any(c => !c.IsDeleted && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> RenameAsync(uint characterId, string oldName, string newName, CancellationToken ct = default)
        => throw NotModelled(nameof(RenameAsync));

    public Task SetBankSlotsAsync(uint characterId, byte slots, CancellationToken ct = default)
        => throw NotModelled(nameof(SetBankSlotsAsync));

    public async Task<IReadOnlyList<Character>> GetByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Character.GetByIds");
        lock (_gate) return [.. ids.Where(_rows.ContainsKey).Select(id => EntityCopy.Clone(_rows[id]))];
    }

    public async Task<int> CountLiveByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Character.CountLiveByIds");
        lock (_gate) return ids.Distinct().Count(id => _rows.TryGetValue(id, out var c) && !c.IsDeleted);
    }

    public async Task<IReadOnlyList<Character>> GetLiveByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Character.GetLiveByIds");
        lock (_gate)
            return [.. ids.Distinct().Where(id => _rows.TryGetValue(id, out var c) && !c.IsDeleted).Select(id => EntityCopy.Clone(_rows[id]))];
    }

    public async Task<IReadOnlyList<Character>> FindByNamesCollatedAsync(IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Character.FindByNamesCollated");
        lock (_gate)
            return [.. _rows.Values.Where(c => !c.IsDeleted
                                               && names.Any(n => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)))
                .Select(EntityCopy.Clone)];
    }
}
