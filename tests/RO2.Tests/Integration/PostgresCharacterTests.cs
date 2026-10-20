using Npgsql;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="CharacterRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers id allocation on create (per-account cap, name length and
/// name-taken checks), live-only reads, soft delete and every <see cref="CharacterRestoreResult"/> outcome,
/// case-sensitive and case-insensitive name lookups, rename, appearance, targeted updates, full-row save,
/// bulk autosave, id-set queries, and uint and ulong round-trips above <see cref="int.MaxValue"/>.
/// </summary>
/// <remarks>
/// Each test uses fresh names and accounts so tests can share one container. Character ids are assigned by
/// the database and read back.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresCharacterTests
{
    private readonly PostgresFixture _fx;
    public PostgresCharacterTests(PostgresFixture fx) => _fx = fx;

    private static long _accounts = Random.Shared.NextInt64(0, 1_000_000_000);

    
    private static uint NewAccount() => (uint)(3_000_000_000L + 10 * Interlocked.Increment(ref _accounts));

    private static string NewName() => "T" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private static async Task<Character> CreateAsync(CharacterRepository repo, uint account, string? name = null,
        byte job = 0, string appearance = "{\"hair\":1}")
    {
        var (character, error) = await repo.CreateCharacterAsync(account, name ?? NewName(), job, 1, appearance);
        Assert.Null(error);
        Assert.NotNull(character);
        return character;
    }

    private static DateTime Micros(DateTime t) => new(t.Ticks - t.Ticks % 10, t.Kind);

    [Fact]
    public async Task Create_assigns_identity_ids_and_the_default_fields()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();

        var a = await CreateAsync(repo, account, job: 1);
        var b = await CreateAsync(repo, account, job: 1);
        Assert.True(a.Id > 0);
        Assert.True(b.Id > a.Id);

        var got = await repo.GetByIdAsync(a.Id);
        Assert.NotNull(got);
        Assert.Equal(a.Id, got.Id);
        Assert.Equal(account, got.AccountId);
        Assert.Equal(a.Name, got.Name);
        Assert.Equal((byte)1, got.Job);
        Assert.Equal((byte)1, got.Gender);
        Assert.Equal((byte)2, got.Race);
        Assert.Equal((byte)0, got.LifeJob);
        Assert.Equal((byte)1, got.Level);
        Assert.Equal((byte)1, got.JobLevel);
        Assert.Equal(1100u, got.MapId);
        Assert.Equal(769f, got.X);
        Assert.Equal(982f, got.Y);
        Assert.Equal(47f, got.Z);
        
        Assert.Equal(120, got.Hp);
        Assert.Equal(120, got.MaxHp);
        Assert.Equal(40, got.Mp);
        Assert.Equal(40, got.MaxMp);
        Assert.Equal(12, got.Str);
        Assert.Equal(10, got.Agi);
        Assert.Equal(12, got.Vit);
        Assert.Equal(8, got.Int);
        Assert.Equal(10, got.Wis);
        Assert.Equal(8, got.Luk);
        Assert.Equal("{\"hair\":1}", got.AppearanceJson);
        Assert.Equal(Character.DefaultEquipHideFlag, got.EquipHideFlag);
        Assert.True(got.AllowAnyoneReview);
        Assert.Equal(Character.DefaultBankSlots, got.BankSlots);
        Assert.Equal(0, got.BonusStr);
        Assert.Equal(0L, got.Gold);
        Assert.Equal(0L, got.BankGold);
        Assert.False(got.IsDeleted);
        Assert.Null(got.DeletedAt);
        Assert.Null(got.LastPlayed);
        Assert.Equal(DateTimeKind.Utc, got.CreatedAt.Kind);
        Assert.Equal(a.CreatedAt, got.CreatedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Create_uses_the_default_stat_table_for_an_unknown_job_and_the_given_stats_when_supplied()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);

        var fallback = await CreateAsync(repo, NewAccount(), job: 200);
        Assert.Equal(100, fallback.Hp);
        Assert.Equal(50, fallback.Mp);
        Assert.Equal(10, fallback.Str);

        var (given, error) = await repo.CreateCharacterAsync(NewAccount(), NewName(), 3, 0, "{}",
            (hp: 500, mp: 60, str: 11, agi: 12, intel: 13, wis: 14, vit: 15, luk: 16));
        Assert.Null(error);
        Assert.NotNull(given);
        var got = await repo.GetByIdAsync(given.Id);
        Assert.NotNull(got);
        Assert.Equal(500, got.Hp);
        Assert.Equal(500, got.MaxHp);
        Assert.Equal(60, got.Mp);
        Assert.Equal(60, got.MaxMp);
        Assert.Equal(11, got.Str);
        Assert.Equal(12, got.Agi);
        Assert.Equal(13, got.Int);
        Assert.Equal(14, got.Wis);
        Assert.Equal(15, got.Vit);
        Assert.Equal(16, got.Luk);
    }

    [Fact]
    public async Task Create_rejects_an_invalid_name_length()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();

        foreach (var bad in new[] { "", "   ", "a", new string('x', 17) })
        {
            var (character, error) = await repo.CreateCharacterAsync(account, bad, 0, 0, "{}");
            Assert.Null(character);
            Assert.Equal("Invalid character name length", error);
        }

        Assert.False(await repo.AccountHasLiveCharacterAsync(account));

        var two = NewName()[..2];
        var (ok, okError) = await repo.CreateCharacterAsync(account, two + new string('y', 14), 0, 0, "{}");
        Assert.Null(okError);
        Assert.NotNull(ok);
    }

    [Fact]
    public async Task Create_rejects_a_live_duplicate_name_case_sensitively()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var name = "Aa" + Guid.NewGuid().ToString("N")[..10];
        await CreateAsync(repo, NewAccount(), name);

        var (character, error) = await repo.CreateCharacterAsync(NewAccount(), name, 0, 0, "{}");
        Assert.Null(character);
        Assert.Equal("Character name already exists", error);

        
        var (other, otherError) = await repo.CreateCharacterAsync(NewAccount(), name.ToUpperInvariant(), 0, 0, "{}");
        Assert.Null(otherError);
        Assert.NotNull(other);
    }

    [Fact]
    public async Task Create_enforces_the_per_account_cap_of_20_live_characters()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();

        var created = new List<Character>();
        for (var i = 0; i < 20; i++) created.Add(await CreateAsync(repo, account));
        Assert.Equal(20, (await repo.GetCharactersByAccountAsync(account)).Count);

        
        var (c21, e21) = await repo.CreateCharacterAsync(account, NewName(), 0, 0, "{}");
        Assert.Null(c21);
        Assert.Equal("Maximum character limit reached", e21);
        var (_, eBad) = await repo.CreateCharacterAsync(account, "x", 0, 0, "{}");
        Assert.Equal("Maximum character limit reached", eBad);

        
        await CreateAsync(repo, NewAccount());
        Assert.True(await repo.DeleteCharacterAsync(created[0].Id, account));
        await CreateAsync(repo, account);
    }

    [Fact]
    public async Task GetById_returns_only_live_characters()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);

        Assert.NotNull(await repo.GetByIdAsync(c.Id));
        Assert.Null(await repo.GetByIdAsync(uint.MaxValue));

        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        Assert.Null(await repo.GetByIdAsync(c.Id));
    }

    [Fact]
    public async Task GetCharactersByAccount_returns_live_characters_oldest_first()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var first = await CreateAsync(repo, account);
        await Task.Delay(5);
        var second = await CreateAsync(repo, account);
        await Task.Delay(5);
        var third = await CreateAsync(repo, account);
        await CreateAsync(repo, NewAccount());
        Assert.True(await repo.DeleteCharacterAsync(second.Id, account));

        var list = await repo.GetCharactersByAccountAsync(account);
        Assert.Equal(new[] { first.Id, third.Id }, list.Select(x => x.Id).ToArray());
        Assert.Empty(await repo.GetCharactersByAccountAsync(NewAccount()));
    }

    [Fact]
    public async Task Delete_is_a_guarded_soft_delete_that_frees_the_name()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);

        
        Assert.False(await repo.DeleteCharacterAsync(c.Id, account + 1));
        Assert.False(await repo.DeleteCharacterAsync(uint.MaxValue - 5, account));
        Assert.NotNull(await repo.GetByIdAsync(c.Id));

        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        
        Assert.False(await repo.DeleteCharacterAsync(c.Id, account));

        var stored = Assert.Single(await repo.GetByIdsAsync([c.Id]));
        Assert.True(stored.IsDeleted);
        Assert.NotNull(stored.DeletedAt);
        Assert.Equal(DateTimeKind.Utc, stored.DeletedAt.Value.Kind);
        Assert.InRange(stored.DeletedAt.Value, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        
        Assert.False(await repo.CharacterNameExistsAsync(c.Name));
        var (again, error) = await repo.CreateCharacterAsync(NewAccount(), c.Name, 0, 0, "{}");
        Assert.Null(error);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Restore_returns_NotFound_for_an_unknown_id_or_the_wrong_account()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);
        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        var cutoff = DateTime.UtcNow.AddHours(-1);

        Assert.Equal(CharacterRestoreResult.NotFound, await repo.TryRestoreCharacterAsync(uint.MaxValue - 7, account, cutoff));
        Assert.Equal(CharacterRestoreResult.NotFound, await repo.TryRestoreCharacterAsync(c.Id, account + 1, cutoff));
    }

    [Fact]
    public async Task Restore_returns_AlreadyLive_for_a_character_that_is_not_deleted()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);

        Assert.Equal(CharacterRestoreResult.AlreadyLive,
            await repo.TryRestoreCharacterAsync(c.Id, account, DateTime.UtcNow.AddHours(-1)));
    }

    [Fact]
    public async Task Restore_returns_Expired_before_the_cutoff_or_without_a_deleted_at_stamp()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);
        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));

        
        Assert.Equal(CharacterRestoreResult.Expired,
            await repo.TryRestoreCharacterAsync(c.Id, account, DateTime.UtcNow.AddHours(1)));

        
        var stored = Assert.Single(await repo.GetByIdsAsync([c.Id]));
        stored.DeletedAt = null;
        await repo.SaveCharacterAsync(stored);
        Assert.Equal(CharacterRestoreResult.Expired,
            await repo.TryRestoreCharacterAsync(c.Id, account, DateTime.UtcNow.AddHours(-1)));
    }

    [Fact]
    public async Task Restore_returns_NameTaken_when_a_live_character_took_the_name()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);
        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        await CreateAsync(repo, NewAccount(), c.Name);

        Assert.Equal(CharacterRestoreResult.NameTaken,
            await repo.TryRestoreCharacterAsync(c.Id, account, DateTime.UtcNow.AddHours(-1)));
        Assert.Null(await repo.GetByIdAsync(c.Id));
    }

    [Fact]
    public async Task Restore_returns_AccountFull_when_the_account_has_20_live_characters()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var victim = await CreateAsync(repo, account);
        for (var i = 0; i < 19; i++) await CreateAsync(repo, account);
        Assert.True(await repo.DeleteCharacterAsync(victim.Id, account));
        await CreateAsync(repo, account);
        Assert.Equal(20, (await repo.GetCharactersByAccountAsync(account)).Count);

        Assert.Equal(CharacterRestoreResult.AccountFull,
            await repo.TryRestoreCharacterAsync(victim.Id, account, DateTime.UtcNow.AddHours(-1)));
    }

    [Fact]
    public async Task Restore_brings_back_the_character_and_a_second_restore_is_AlreadyLive()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);
        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        var cutoff = DateTime.UtcNow.AddHours(-1);

        Assert.Equal(CharacterRestoreResult.Restored, await repo.TryRestoreCharacterAsync(c.Id, account, cutoff));
        var got = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(got);
        Assert.False(got.IsDeleted);
        Assert.Null(got.DeletedAt);
        Assert.Equal(c.Name, got.Name);
        Assert.True(await repo.CharacterNameExistsAsync(c.Name));

        Assert.Equal(CharacterRestoreResult.AlreadyLive, await repo.TryRestoreCharacterAsync(c.Id, account, cutoff));
    }

    [Fact]
    public async Task Restore_honours_a_cutoff_equal_to_the_deleted_at_instant()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);
        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        var stored = Assert.Single(await repo.GetByIdsAsync([c.Id]));

        
        Assert.Equal(CharacterRestoreResult.Restored,
            await repo.TryRestoreCharacterAsync(c.Id, account, stored.DeletedAt!.Value));
    }

    [Fact]
    public async Task CharacterNameExists_is_case_sensitive_and_live_only()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var name = "Mm" + Guid.NewGuid().ToString("N")[..10];
        var c = await CreateAsync(repo, account, name);

        Assert.True(await repo.CharacterNameExistsAsync(name));
        Assert.False(await repo.CharacterNameExistsAsync(name.ToUpperInvariant()));
        Assert.False(await repo.CharacterNameExistsAsync(name.ToLowerInvariant()));
        Assert.False(await repo.CharacterNameExistsAsync(NewName()));

        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        Assert.False(await repo.CharacterNameExistsAsync(name));
    }

    [Fact]
    public async Task NameExistsIgnoreCase_matches_any_case_skips_blank_and_deleted()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var name = "Nn" + Guid.NewGuid().ToString("N")[..10];
        var c = await CreateAsync(repo, account, name);

        Assert.True(await repo.NameExistsIgnoreCaseAsync(name));
        Assert.True(await repo.NameExistsIgnoreCaseAsync(name.ToUpperInvariant()));
        Assert.True(await repo.NameExistsIgnoreCaseAsync(name.ToLowerInvariant()));
        Assert.False(await repo.NameExistsIgnoreCaseAsync(name + "x"));
        Assert.False(await repo.NameExistsIgnoreCaseAsync(""));
        Assert.False(await repo.NameExistsIgnoreCaseAsync("   "));

        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        Assert.False(await repo.NameExistsIgnoreCaseAsync(name));
    }

    [Fact]
    public async Task FindByNameIgnoreCase_and_FindByExactName_return_the_live_character()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var name = "Ff" + Guid.NewGuid().ToString("N")[..10];
        var c = await CreateAsync(repo, account, name);

        var ignoreCase = await repo.FindByNameIgnoreCaseAsync(name.ToLowerInvariant());
        Assert.NotNull(ignoreCase);
        Assert.Equal(c.Id, ignoreCase.Id);
        Assert.Equal(name, ignoreCase.Name); 

        var exact = await repo.FindByExactNameAsync(name);
        Assert.NotNull(exact);
        Assert.Equal(c.Id, exact.Id);
        Assert.Null(await repo.FindByExactNameAsync(name.ToLowerInvariant()));
        Assert.Null(await repo.FindByNameIgnoreCaseAsync(name + "z"));
        Assert.Null(await repo.FindByExactNameAsync(name + "z"));

        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        Assert.Null(await repo.FindByNameIgnoreCaseAsync(name));
        Assert.Null(await repo.FindByExactNameAsync(name));
    }

    [Fact]
    public async Task Name_lookups_treat_regex_and_like_metacharacters_literally()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var dotted = $"A.{tag}";
        await CreateAsync(repo, NewAccount(), dotted);

        Assert.True(await repo.NameExistsIgnoreCaseAsync(dotted.ToLowerInvariant()));
        Assert.False(await repo.NameExistsIgnoreCaseAsync($"AX{tag}"));
        Assert.False(await repo.NameExistsIgnoreCaseAsync($"%{tag}"));
        Assert.False(await repo.NameExistsIgnoreCaseAsync($"_.{tag}"));
    }

    [Fact]
    public async Task AccountHasLiveCharacter_is_true_only_while_one_is_not_deleted()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        Assert.False(await repo.AccountHasLiveCharacterAsync(account));

        var c = await CreateAsync(repo, account);
        Assert.True(await repo.AccountHasLiveCharacterAsync(account));

        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        Assert.False(await repo.AccountHasLiveCharacterAsync(account));
    }

    [Fact]
    public async Task Rename_is_guarded_on_the_old_name_and_not_deleted()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);
        var renamed = NewName();

        Assert.False(await repo.RenameAsync(c.Id, "not-the-name", renamed));
        Assert.False(await repo.RenameAsync(uint.MaxValue - 9, c.Name, renamed));
        Assert.Equal(c.Name, (await repo.GetByIdAsync(c.Id))!.Name);

        Assert.True(await repo.RenameAsync(c.Id, c.Name, renamed));
        Assert.Equal(renamed, (await repo.GetByIdAsync(c.Id))!.Name);
        Assert.False(await repo.CharacterNameExistsAsync(c.Name));
        Assert.True(await repo.CharacterNameExistsAsync(renamed));

        
        Assert.False(await repo.RenameAsync(c.Id, c.Name, NewName()));

        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        Assert.False(await repo.RenameAsync(c.Id, renamed, NewName()));
    }

    [Fact]
    public async Task UpdateAppearance_matches_non_deleted_rows_even_when_the_value_is_unchanged()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account, appearance: "{\"a\": 1,  \"b\":[2]}");

        Assert.True(await repo.UpdateAppearanceAsync(c.Id, "{\"z\":  9, \"y\":1}", 0, 1));
        var got = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(got);
        Assert.Equal("{\"z\":  9, \"y\":1}", got.AppearanceJson); 
        Assert.Equal((byte)0, got.Gender);
        Assert.Equal((byte)1, got.Race);

        
        Assert.True(await repo.UpdateAppearanceAsync(c.Id, "{\"z\":  9, \"y\":1}", 0, 1));
        Assert.False(await repo.UpdateAppearanceAsync(uint.MaxValue - 11, "{}", 0, 1));

        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        Assert.False(await repo.UpdateAppearanceAsync(c.Id, "{}", 1, 2));
    }

    [Fact]
    public async Task UpdateAppearanceAndJob_sets_the_json_and_the_job_only_when_given()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account, job: 2);

        await repo.UpdateAppearanceAndJobAsync(c.Id, "{\"new\":1}", null);
        var noJob = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(noJob);
        Assert.Equal("{\"new\":1}", noJob.AppearanceJson);
        Assert.Equal((byte)2, noJob.Job);

        await repo.UpdateAppearanceAndJobAsync(c.Id, "{\"newer\":2}", 5);
        var withJob = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(withJob);
        Assert.Equal("{\"newer\":2}", withJob.AppearanceJson);
        Assert.Equal((byte)5, withJob.Job);

        await repo.UpdateAppearanceAndJobAsync(c.Id, null, 6);
        var nullJson = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(nullJson);
        Assert.Null(nullJson.AppearanceJson);
        Assert.Equal((byte)6, nullJson.Job);

        
        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));
        await repo.UpdateAppearanceAndJobAsync(c.Id, "{\"deleted\":1}", 7);
        var deleted = Assert.Single(await repo.GetByIdsAsync([c.Id]));
        Assert.Equal("{\"deleted\":1}", deleted.AppearanceJson);
        Assert.Equal((byte)7, deleted.Job);
    }

    [Fact]
    public async Task UpdatePosition_Stats_and_Gold_round_trip_and_stamp_last_played()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var c = await CreateAsync(repo, NewAccount());

        await repo.UpdatePositionAsync(c.Id, 4_000_000_001u, 1.5f, -2.25f, 300.75f);
        var pos = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(pos);
        Assert.Equal(4_000_000_001u, pos.MapId);
        Assert.Equal(1.5f, pos.X);
        Assert.Equal(-2.25f, pos.Y);
        Assert.Equal(300.75f, pos.Z);
        Assert.NotNull(pos.LastPlayed);
        Assert.Equal(DateTimeKind.Utc, pos.LastPlayed.Value.Kind);
        Assert.InRange(pos.LastPlayed.Value, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        await repo.UpdateStatsAsync(c.Id, 77, 33, 99, ulong.MaxValue);
        var stats = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(stats);
        Assert.Equal(77, stats.Hp);
        Assert.Equal(33, stats.Mp);
        Assert.Equal((byte)99, stats.Level);
        Assert.Equal(ulong.MaxValue, stats.Exp);
        Assert.Equal(c.MaxHp, stats.MaxHp);

        await repo.UpdateGoldAsync(c.Id, long.MaxValue - 1);
        var gold = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(gold);
        Assert.Equal(long.MaxValue - 1, gold.Gold);
        Assert.Equal(ulong.MaxValue, gold.Exp); 

        await repo.UpdateGoldAsync(c.Id, -5);
        Assert.Equal(-5L, (await repo.GetByIdAsync(c.Id))!.Gold);
    }

    [Fact]
    public async Task UpdateEquipHideFlag_AllowAnyoneReview_and_BankSlots_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var c = await CreateAsync(repo, NewAccount());

        await repo.UpdateEquipHideFlagAsync(c.Id, ushort.MaxValue);
        Assert.Equal(ushort.MaxValue, (await repo.GetByIdAsync(c.Id))!.EquipHideFlag);
        await repo.UpdateEquipHideFlagAsync(c.Id, 0x0177);
        Assert.Equal((ushort)0x0177, (await repo.GetByIdAsync(c.Id))!.EquipHideFlag);

        await repo.UpdateAllowAnyoneReviewAsync(c.Id, false);
        Assert.False((await repo.GetByIdAsync(c.Id))!.AllowAnyoneReview);
        await repo.UpdateAllowAnyoneReviewAsync(c.Id, true);
        Assert.True((await repo.GetByIdAsync(c.Id))!.AllowAnyoneReview);

        await repo.SetBankSlotsAsync(c.Id, 255);
        Assert.Equal((byte)255, (await repo.GetByIdAsync(c.Id))!.BankSlots);
        await repo.SetBankSlotsAsync(c.Id, 60);
        Assert.Equal((byte)60, (await repo.GetByIdAsync(c.Id))!.BankSlots);
    }

    [Fact]
    public async Task Targeted_updates_filter_on_the_id_only_and_ignore_unknown_ids()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);
        Assert.True(await repo.DeleteCharacterAsync(c.Id, account));

        
        await repo.UpdateGoldAsync(c.Id, 1234);
        await repo.SetBankSlotsAsync(c.Id, 33);
        var stored = Assert.Single(await repo.GetByIdsAsync([c.Id]));
        Assert.Equal(1234L, stored.Gold);
        Assert.Equal((byte)33, stored.BankSlots);
        Assert.True(stored.IsDeleted);

        
        const uint ghost = uint.MaxValue - 13;
        await repo.UpdateGoldAsync(ghost, 1);
        await repo.UpdatePositionAsync(ghost, 1, 1, 1, 1);
        await repo.UpdateStatsAsync(ghost, 1, 1, 1, 1);
        await repo.UpdateEquipHideFlagAsync(ghost, 1);
        await repo.UpdateAllowAnyoneReviewAsync(ghost, false);
        await repo.SetBankSlotsAsync(ghost, 1);
        await repo.UpdateAppearanceAndJobAsync(ghost, "{}", 1);
        Assert.Empty(await repo.GetByIdsAsync([ghost]));
    }

    [Fact]
    public async Task Save_replaces_every_field_and_stamps_last_played()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var c = await CreateAsync(repo, account);
        var created = Micros(DateTime.UtcNow.AddDays(-3));
        var deletedAt = Micros(DateTime.UtcNow.AddDays(-1));

        var full = new Character
        {
            Id = c.Id,
            AccountId = account + 1,
            Name = NewName(),
            Job = 9,
            Race = 1,
            LifeJob = 4,
            Gender = 0,
            Level = 255,
            Exp = ulong.MaxValue,
            JobLevel = 77,
            JobExp = 9_223_372_036_854_775_808UL,
            MasterLevel = ushort.MaxValue,
            MapId = uint.MaxValue,
            X = 1.25f,
            Y = -3.5f,
            Z = 1e6f,
            Hp = int.MaxValue,
            MaxHp = int.MinValue,
            Mp = 11,
            MaxMp = 12,
            Str = 21,
            Agi = 22,
            Vit = 23,
            Int = 24,
            Wis = 25,
            Luk = 26,
            BonusStr = 31,
            BonusAgi = 32,
            BonusInt = 33,
            BonusWis = 34,
            BonusVit = 35,
            BonusLuk = 36,
            Gold = long.MaxValue,
            BankGold = long.MinValue,
            BindIndex = ushort.MaxValue,
            FacingYaw = 3.14159f,
            AppearanceJson = "{\"full\": true}",
            EquipHideFlag = 0xFFFF,
            AllowAnyoneReview = false,
            BankSlots = 200,
            CreatedAt = created,
            PlayTimeSeconds = 86_400,
            IsDeleted = true,
            DeletedAt = deletedAt,
        };
        await repo.SaveCharacterAsync(full);
        Assert.NotNull(full.LastPlayed);

        var got = Assert.Single(await repo.GetByIdsAsync([c.Id]));
        Assert.Equal(account + 1, got.AccountId);
        Assert.Equal(full.Name, got.Name);
        Assert.Equal((byte)9, got.Job);
        Assert.Equal((byte)1, got.Race);
        Assert.Equal((byte)4, got.LifeJob);
        Assert.Equal((byte)0, got.Gender);
        Assert.Equal((byte)255, got.Level);
        Assert.Equal(ulong.MaxValue, got.Exp);
        Assert.Equal((byte)77, got.JobLevel);
        Assert.Equal(9_223_372_036_854_775_808UL, got.JobExp);
        Assert.Equal(ushort.MaxValue, got.MasterLevel);
        Assert.Equal(uint.MaxValue, got.MapId);
        Assert.Equal(1.25f, got.X);
        Assert.Equal(-3.5f, got.Y);
        Assert.Equal(1e6f, got.Z);
        Assert.Equal(int.MaxValue, got.Hp);
        Assert.Equal(int.MinValue, got.MaxHp);
        Assert.Equal(11, got.Mp);
        Assert.Equal(12, got.MaxMp);
        Assert.Equal(21, got.Str);
        Assert.Equal(22, got.Agi);
        Assert.Equal(23, got.Vit);
        Assert.Equal(24, got.Int);
        Assert.Equal(25, got.Wis);
        Assert.Equal(26, got.Luk);
        Assert.Equal(31, got.BonusStr);
        Assert.Equal(32, got.BonusAgi);
        Assert.Equal(33, got.BonusInt);
        Assert.Equal(34, got.BonusWis);
        Assert.Equal(35, got.BonusVit);
        Assert.Equal(36, got.BonusLuk);
        Assert.Equal(long.MaxValue, got.Gold);
        Assert.Equal(long.MinValue, got.BankGold);
        Assert.Equal(ushort.MaxValue, got.BindIndex);
        Assert.Equal(3.14159f, got.FacingYaw);
        Assert.Equal("{\"full\": true}", got.AppearanceJson);
        Assert.Equal((ushort)0xFFFF, got.EquipHideFlag);
        Assert.False(got.AllowAnyoneReview);
        Assert.Equal((byte)200, got.BankSlots);
        Assert.Equal(created, got.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, got.CreatedAt.Kind);
        Assert.NotNull(got.LastPlayed);
        Assert.Equal(full.LastPlayed.Value, got.LastPlayed.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(86_400L, got.PlayTimeSeconds);
        Assert.True(got.IsDeleted);
        Assert.Equal(deletedAt, got.DeletedAt);

        
        full.AppearanceJson = null;
        full.IsDeleted = false;
        full.DeletedAt = null;
        await repo.SaveCharacterAsync(full);
        var cleared = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(cleared);
        Assert.Null(cleared.AppearanceJson);
        Assert.Null(cleared.DeletedAt);
    }

    [Fact]
    public async Task Save_normalizes_non_utc_timestamps_and_does_not_upsert_an_unknown_id()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var c = await CreateAsync(repo, NewAccount());

        var got = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(got);
        var local = Micros(DateTime.UtcNow.AddDays(-2)).ToLocalTime();
        var unspecified = DateTime.SpecifyKind(Micros(DateTime.UtcNow.AddDays(-1)), DateTimeKind.Unspecified);
        got.CreatedAt = local;
        got.DeletedAt = unspecified;
        await repo.SaveCharacterAsync(got);

        var back = await repo.GetByIdAsync(c.Id);
        Assert.NotNull(back);
        Assert.Equal(local.ToUniversalTime(), back.CreatedAt);
        Assert.Equal(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), back.DeletedAt);

        var ghost = new Character { Id = uint.MaxValue - 17, AccountId = NewAccount(), Name = NewName() };
        await repo.SaveCharacterAsync(ghost);
        Assert.Empty(await repo.GetByIdsAsync([ghost.Id]));
    }

    [Fact]
    public async Task BulkSavePlayerState_writes_many_rows_in_one_batch_and_ignores_empty_and_unknown()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var a = await CreateAsync(repo, account);
        var b = await CreateAsync(repo, account);
        var untouched = await CreateAsync(repo, account);

        await repo.BulkSavePlayerStateAsync([]);

        await repo.BulkSavePlayerStateAsync(
        [
            new PlayerStateSave(a.Id, 4_100_000_000u, 1.5f, 2.5f, 3.5f, 11, 111, 12, 112, 20, ulong.MaxValue, 1000, 2000, 5, 65535, 0.5f),
            new PlayerStateSave(b.Id, 1200, -1f, -2f, -3f, 21, 221, 22, 222, 30, 123_456_789_012UL, -50, 60, 6, 7, 1.5f),
            new PlayerStateSave(uint.MaxValue - 19, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1f),
        ]);

        var ga = await repo.GetByIdAsync(a.Id);
        Assert.NotNull(ga);
        Assert.Equal(4_100_000_000u, ga.MapId);
        Assert.Equal(1.5f, ga.X);
        Assert.Equal(2.5f, ga.Y);
        Assert.Equal(3.5f, ga.Z);
        Assert.Equal(11, ga.Hp);
        Assert.Equal(111, ga.MaxHp);
        Assert.Equal(12, ga.Mp);
        Assert.Equal(112, ga.MaxMp);
        Assert.Equal((byte)20, ga.Level);
        Assert.Equal(ulong.MaxValue, ga.Exp);
        Assert.Equal(1000L, ga.Gold);
        Assert.Equal(2000L, ga.BankGold);
        Assert.Equal((byte)5, ga.Job);
        Assert.Equal(ushort.MaxValue, ga.BindIndex);
        Assert.Equal(0.5f, ga.FacingYaw);
        Assert.NotNull(ga.LastPlayed);

        var gb = await repo.GetByIdAsync(b.Id);
        Assert.NotNull(gb);
        Assert.Equal(1200u, gb.MapId);
        Assert.Equal(-3f, gb.Z);
        Assert.Equal(123_456_789_012UL, gb.Exp);
        Assert.Equal(-50L, gb.Gold);
        Assert.Equal((byte)6, gb.Job);
        Assert.Equal((ushort)7, gb.BindIndex);

        var gu = await repo.GetByIdAsync(untouched.Id);
        Assert.NotNull(gu);
        Assert.Equal(1100u, gu.MapId);
        Assert.Null(gu.LastPlayed);
    }

    [Fact]
    public async Task GetByIds_CountLiveByIds_and_GetLiveByIds_split_live_from_deleted()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var live1 = await CreateAsync(repo, account);
        var live2 = await CreateAsync(repo, account);
        var dead = await CreateAsync(repo, account);
        var other = await CreateAsync(repo, account);
        Assert.True(await repo.DeleteCharacterAsync(dead.Id, account));
        uint[] ids = [live1.Id, live2.Id, dead.Id, uint.MaxValue - 23];

        var all = await repo.GetByIdsAsync(ids);
        Assert.Equal(3, all.Count);
        Assert.Single(all, x => x.Id == dead.Id && x.IsDeleted);
        Assert.DoesNotContain(all, x => x.Id == other.Id);

        Assert.Equal(2, await repo.CountLiveByIdsAsync(ids));
        var live = await repo.GetLiveByIdsAsync(ids);
        Assert.Equal(new[] { live1.Id, live2.Id }.Order(), live.Select(x => x.Id).Order());

        Assert.Empty(await repo.GetByIdsAsync([]));
        Assert.Equal(0, await repo.CountLiveByIdsAsync([]));
        Assert.Empty(await repo.GetLiveByIdsAsync([]));
    }

    [Fact]
    public async Task FindByNamesCollated_matches_case_insensitively_returns_stored_spelling_and_skips_deleted()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = NewAccount();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var one = await CreateAsync(repo, account, "iRO2" + tag);
        var two = await CreateAsync(repo, account, "Zed" + tag);
        var dead = await CreateAsync(repo, account, "Dead" + tag);
        Assert.True(await repo.DeleteCharacterAsync(dead.Id, account));

        var found = await repo.FindByNamesCollatedAsync(["iro2" + tag, "ZED" + tag, "dead" + tag, "nobody" + tag]);
        Assert.Equal(2, found.Count);
        Assert.Single(found, x => x.Id == one.Id && x.Name == "iRO2" + tag);
        Assert.Single(found, x => x.Id == two.Id && x.Name == "Zed" + tag);

        Assert.Empty(await repo.FindByNamesCollatedAsync([]));
    }

    [Fact]
    public async Task Unsigned_ids_above_int_max_round_trip_through_every_id_column()
    {
        if (!_fx.Available) return;
        var repo = new CharacterRepository(_fx.Db!);
        var account = uint.MaxValue - (uint)Random.Shared.Next(1000, 1_000_000);
        var name = NewName();

        
        var bigId = 4_000_000_000L + Random.Shared.NextInt64(0, 200_000_000);
        await using (var conn = await _fx.Db!.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO characters (id, account_id, name, map_id) OVERRIDING SYSTEM VALUE VALUES (@id, @a, @n, @m)", conn))
        {
            cmd.Parameters.AddWithValue("id", bigId);
            cmd.Parameters.AddWithValue("a", (long)account);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("m", 4_294_967_295L);
            await cmd.ExecuteNonQueryAsync();
        }

        var id = (uint)bigId;
        Assert.True(id > int.MaxValue);
        var got = await repo.GetByIdAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got.Id);
        Assert.Equal(account, got.AccountId);
        Assert.Equal(uint.MaxValue, got.MapId);
        Assert.True((await repo.GetCharactersByAccountAsync(account)).Count >= 1);
        Assert.True(await repo.AccountHasLiveCharacterAsync(account));
        Assert.Equal(1, await repo.CountLiveByIdsAsync([id]));
        Assert.Single(await repo.GetLiveByIdsAsync([id]));

        await repo.UpdateStatsAsync(id, 1, 2, 3, ulong.MaxValue);
        await repo.UpdateGoldAsync(id, 99);
        Assert.Equal(ulong.MaxValue, (await repo.GetByIdAsync(id))!.Exp);

        Assert.True(await repo.DeleteCharacterAsync(id, account));
        Assert.Equal(CharacterRestoreResult.Restored,
            await repo.TryRestoreCharacterAsync(id, account, DateTime.UtcNow.AddHours(-1)));
        Assert.NotNull(await repo.GetByIdAsync(id));
    }
}
