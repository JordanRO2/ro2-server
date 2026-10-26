using RO2.Persistence.Postgres.Repositories;
using RO2.Skills.Infrastructure.Postgres;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="SkillRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers learn (insert and raise-only), update, per-character reads and batch delete. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresSkillTests
{
    private readonly PostgresFixture _fx;
    public PostgresSkillTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(3_000_000_000L, 4_200_000_000L);

    [Fact]
    public async Task Get_unknown_character_is_empty()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        Assert.Empty(await repo.GetSkillsAsync(NewCharacterId()));
    }

    [Fact]
    public async Task Learn_inserts_new_skill_and_get_returns_it()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        await repo.LearnSkillAsync(ch, 4_000_000_001u, 3);

        var skills = await repo.GetSkillsAsync(ch);
        var s = Assert.Single(skills);
        Assert.Equal(ch, s.CharacterId);
        Assert.Equal(4_000_000_001u, s.SkillId);
        Assert.Equal((byte)3, s.Level);
        Assert.True(s.Id > 0);
    }

    [Fact]
    public async Task Learn_existing_with_higher_level_raises_it()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        await repo.LearnSkillAsync(ch, 10, 2);
        var idBefore = Assert.Single(await repo.GetSkillsAsync(ch)).Id;
        await repo.LearnSkillAsync(ch, 10, 5);

        var s = Assert.Single(await repo.GetSkillsAsync(ch));
        Assert.Equal((byte)5, s.Level);
        Assert.Equal(idBefore, s.Id); 
    }

    [Fact]
    public async Task Learn_existing_with_equal_or_lower_level_is_noop()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        await repo.LearnSkillAsync(ch, 10, 5);
        await repo.LearnSkillAsync(ch, 10, 5); 
        await repo.LearnSkillAsync(ch, 10, 1); 

        var s = Assert.Single(await repo.GetSkillsAsync(ch));
        Assert.Equal((byte)5, s.Level);
    }

    [Fact]
    public async Task Learn_level_byte_max_roundtrips()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        await repo.LearnSkillAsync(ch, 7, 255);
        Assert.Equal((byte)255, Assert.Single(await repo.GetSkillsAsync(ch)).Level);
    }

    [Fact]
    public async Task Update_sets_level_even_when_lower()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        await repo.LearnSkillAsync(ch, 20, 9);
        await repo.UpdateSkillLevelAsync(ch, 20, 4);
        Assert.Equal((byte)4, Assert.Single(await repo.GetSkillsAsync(ch)).Level);

        await repo.UpdateSkillLevelAsync(ch, 20, 8);
        Assert.Equal((byte)8, Assert.Single(await repo.GetSkillsAsync(ch)).Level);
    }

    [Fact]
    public async Task Update_unlearned_skill_does_not_insert()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        await repo.UpdateSkillLevelAsync(ch, 99, 3);
        Assert.Empty(await repo.GetSkillsAsync(ch));
    }

    [Fact]
    public async Task Update_touches_only_the_targeted_skill_and_character()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        var other = NewCharacterId();
        await repo.LearnSkillAsync(ch, 1, 1);
        await repo.LearnSkillAsync(ch, 2, 1);
        await repo.LearnSkillAsync(other, 1, 1);

        await repo.UpdateSkillLevelAsync(ch, 1, 7);

        var mine = await repo.GetSkillsAsync(ch);
        Assert.Equal((byte)7, mine.Single(x => x.SkillId == 1).Level);
        Assert.Equal((byte)1, mine.Single(x => x.SkillId == 2).Level);
        Assert.Equal((byte)1, Assert.Single(await repo.GetSkillsAsync(other)).Level);
    }

    [Fact]
    public async Task Get_returns_only_that_character_in_insertion_order_with_ascending_ids()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        var other = NewCharacterId();
        await repo.LearnSkillAsync(ch, 30, 1);
        await repo.LearnSkillAsync(other, 30, 1);
        await repo.LearnSkillAsync(ch, 10, 2);
        await repo.LearnSkillAsync(ch, 20, 3);

        var skills = await repo.GetSkillsAsync(ch);
        Assert.Equal(new uint[] { 30, 10, 20 }, skills.Select(x => x.SkillId).ToArray());
        Assert.All(skills, x => Assert.Equal(ch, x.CharacterId));
        Assert.True(skills[0].Id < skills[1].Id && skills[1].Id < skills[2].Id);
    }

    [Fact]
    public async Task Delete_removes_only_listed_skills_of_that_character()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        var other = NewCharacterId();
        await repo.LearnSkillAsync(ch, 1, 1);
        await repo.LearnSkillAsync(ch, 2, 2);
        await repo.LearnSkillAsync(ch, 3, 3);
        await repo.LearnSkillAsync(other, 1, 4);
        await repo.LearnSkillAsync(other, 2, 4);

        await repo.DeleteSkillsAsync(ch, new uint[] { 1, 3, 77 }); 

        var left = Assert.Single(await repo.GetSkillsAsync(ch));
        Assert.Equal(2u, left.SkillId);
        Assert.Equal(2, (await repo.GetSkillsAsync(other)).Count); 
    }

    [Fact]
    public async Task Delete_with_empty_list_removes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        await repo.LearnSkillAsync(ch, 1, 1);
        await repo.DeleteSkillsAsync(ch, Array.Empty<uint>());
        Assert.Single(await repo.GetSkillsAsync(ch));
    }

    [Fact]
    public async Task Delete_large_skill_ids_and_relearn_after_delete()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        await repo.LearnSkillAsync(ch, 4_100_000_000u, 6);
        await repo.DeleteSkillsAsync(ch, new[] { 4_100_000_000u });
        Assert.Empty(await repo.GetSkillsAsync(ch));

        
        await repo.LearnSkillAsync(ch, 4_100_000_000u, 1);
        Assert.Equal((byte)1, Assert.Single(await repo.GetSkillsAsync(ch)).Level);
    }

    [Fact]
    public async Task Concurrent_learn_of_same_skill_leaves_one_row_with_highest_level()
    {
        if (!_fx.Available) return;
        var repo = new SkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        await Task.WhenAll(Enumerable.Range(1, 8).Select(l => repo.LearnSkillAsync(ch, 5, (byte)l)));

        var s = Assert.Single(await repo.GetSkillsAsync(ch));
        Assert.Equal((byte)8, s.Level);
    }
}
