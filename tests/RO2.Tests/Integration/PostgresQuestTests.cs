using Npgsql;
using RO2.Domain.Enums;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="QuestRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers accept, complete and give-up, the in-progress-only compare-and-set, tracers, progress save and the daily-gate timestamp. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresQuestTests
{
    private readonly PostgresFixture _fx;
    public PostgresQuestTests(PostgresFixture fx) => _fx = fx;

    private static uint NewChar() => (uint)Random.Shared.NextInt64(int.MaxValue + 1L, uint.MaxValue);

    private sealed record Row(long Id, short Status, bool IsTraced, string? ProgressJson, DateTime? CompletedAt);

    private async Task<List<Row>> RawRowsAsync(uint characterId, uint questId)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, status, is_traced, progress_json, completed_at FROM character_quests " +
            "WHERE character_id = @c AND quest_id = @q ORDER BY id", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("q", (long)questId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Row>();
        while (await reader.ReadAsync())
        {
            rows.Add(new Row(
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
        }
        return rows;
    }

    [Fact]
    public async Task Accept_creates_in_progress_quest_with_defaults()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        var before = DateTime.UtcNow.AddSeconds(-5);

        await repo.AcceptQuestAsync(c, 1234);

        var active = await repo.GetActiveQuestsAsync(c);
        var q = Assert.Single(active);
        Assert.True(q.Id > 0);
        Assert.Equal(c, q.CharacterId);
        Assert.Equal(1234u, q.QuestId);
        Assert.Equal(QuestStatus.InProgress, q.Status);
        Assert.Null(q.ProgressJson);
        Assert.False(q.IsTraced);
        Assert.Null(q.CompletedAt);
        Assert.Equal(DateTimeKind.Utc, q.StartedAt.Kind);
        Assert.InRange(q.StartedAt, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Accept_assigns_increasing_distinct_ids()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 1);
        await repo.AcceptQuestAsync(c, 2);
        await repo.AcceptQuestAsync(c, 3);

        var active = await repo.GetActiveQuestsAsync(c);
        Assert.Equal(3, active.Count);
        Assert.True(active[0].Id < active[1].Id);
        Assert.True(active[1].Id < active[2].Id);
    }

    [Fact]
    public async Task Large_ids_round_trip_as_uint()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, uint.MaxValue);

        var q = Assert.Single(await repo.GetActiveQuestsAsync(c));
        Assert.Equal(uint.MaxValue, q.QuestId);

        await repo.CompleteQuestAsync(c, uint.MaxValue);
        Assert.Equal(new[] { uint.MaxValue }, await repo.GetCompletedQuestIdsAsync(c));
        Assert.NotNull(await repo.GetLastCompletedAtAsync(c, uint.MaxValue));
    }

    [Fact]
    public async Task Active_quests_are_sorted_by_started_at()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 30);
        await Task.Delay(10);
        await repo.AcceptQuestAsync(c, 10);
        await Task.Delay(10);
        await repo.AcceptQuestAsync(c, 20);

        var active = await repo.GetActiveQuestsAsync(c);
        Assert.Equal(new[] { 30u, 10u, 20u }, active.Select(a => a.QuestId).ToArray());
        Assert.True(active[0].StartedAt <= active[1].StartedAt);
        Assert.True(active[1].StartedAt <= active[2].StartedAt);
    }

    [Fact]
    public async Task Active_excludes_completed_and_other_characters()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var a = NewChar();
        var b = NewChar();
        await repo.AcceptQuestAsync(a, 1);
        await repo.AcceptQuestAsync(a, 2);
        await repo.AcceptQuestAsync(a, 3);
        await repo.CompleteQuestAsync(a, 3);
        await repo.AcceptQuestAsync(b, 4);

        var activeA = await repo.GetActiveQuestsAsync(a);
        Assert.Equal(new[] { 1u, 2u }, activeA.Select(q => q.QuestId).OrderBy(x => x).ToArray());
        var activeB = Assert.Single(await repo.GetActiveQuestsAsync(b));
        Assert.Equal(4u, activeB.QuestId);
    }

    [Fact]
    public async Task Empty_character_has_no_quests()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        Assert.Empty(await repo.GetActiveQuestsAsync(c));
        Assert.Empty(await repo.GetCompletedQuestIdsAsync(c));
        Assert.Null(await repo.GetLastCompletedAtAsync(c, 5));
    }

    [Fact]
    public async Task Complete_moves_quest_from_active_to_completed()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 1);
        await repo.AcceptQuestAsync(c, 2);
        await repo.AcceptQuestAsync(c, 3);

        await repo.CompleteQuestAsync(c, 1);
        await repo.CompleteQuestAsync(c, 3);

        Assert.Equal(new[] { 1u, 3u }, await repo.GetCompletedQuestIdsAsync(c));
        var active = Assert.Single(await repo.GetActiveQuestsAsync(c));
        Assert.Equal(2u, active.QuestId);
    }

    [Fact]
    public async Task Complete_without_in_progress_run_is_silent_noop()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.CompleteQuestAsync(c, 99); 

        Assert.Empty(await repo.GetCompletedQuestIdsAsync(c));
        Assert.Empty(await repo.GetActiveQuestsAsync(c));
        Assert.Empty(await RawRowsAsync(c, 99));
    }

    [Fact]
    public async Task Complete_twice_keeps_first_completion_time()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 7);
        await repo.CompleteQuestAsync(c, 7);
        var first = await repo.GetLastCompletedAtAsync(c, 7);
        Assert.NotNull(first);

        await Task.Delay(20);
        await repo.CompleteQuestAsync(c, 7); 

        Assert.Equal(first, await repo.GetLastCompletedAtAsync(c, 7));
        Assert.Single(await repo.GetCompletedQuestIdsAsync(c));
    }

    [Fact]
    public async Task Complete_touches_only_one_of_duplicate_in_progress_runs()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 5);
        await repo.AcceptQuestAsync(c, 5);

        await repo.CompleteQuestAsync(c, 5);

        Assert.Single(await repo.GetActiveQuestsAsync(c));
        Assert.Single(await repo.GetCompletedQuestIdsAsync(c));
        var rows = await RawRowsAsync(c, 5);
        Assert.Equal(2, rows.Count);
        Assert.Equal((short)QuestStatus.Completed, rows[0].Status); 
        Assert.Equal((short)QuestStatus.InProgress, rows[1].Status);
    }

    [Fact]
    public async Task Complete_does_not_affect_other_characters()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var a = NewChar();
        var b = NewChar();
        await repo.AcceptQuestAsync(a, 8);
        await repo.AcceptQuestAsync(b, 8);

        await repo.CompleteQuestAsync(a, 8);

        Assert.Empty(await repo.GetActiveQuestsAsync(a));
        Assert.Single(await repo.GetActiveQuestsAsync(b));
        Assert.Empty(await repo.GetCompletedQuestIdsAsync(b));
    }

    [Fact]
    public async Task Last_completed_at_is_null_while_only_in_progress_or_for_other_quest()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 11);
        Assert.Null(await repo.GetLastCompletedAtAsync(c, 11)); 

        await repo.CompleteQuestAsync(c, 11);
        Assert.Null(await repo.GetLastCompletedAtAsync(c, 12)); 
    }

    [Fact]
    public async Task Last_completed_at_is_recent_utc_after_completion()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        var before = DateTime.UtcNow.AddSeconds(-5);
        await repo.AcceptQuestAsync(c, 21);
        await repo.CompleteQuestAsync(c, 21);

        var at = await repo.GetLastCompletedAtAsync(c, 21);
        Assert.NotNull(at);
        Assert.Equal(DateTimeKind.Utc, at.Value.Kind);
        Assert.InRange(at.Value, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Repeat_quest_keeps_each_run_and_reports_latest_completion()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 40);
        await repo.CompleteQuestAsync(c, 40);
        var first = await repo.GetLastCompletedAtAsync(c, 40);
        Assert.NotNull(first);

        await Task.Delay(20);
        await repo.AcceptQuestAsync(c, 40);   
        Assert.Single(await repo.GetActiveQuestsAsync(c));
        await repo.CompleteQuestAsync(c, 40);

        Assert.Equal(new[] { 40u, 40u }, await repo.GetCompletedQuestIdsAsync(c)); 
        var second = await repo.GetLastCompletedAtAsync(c, 40);
        Assert.NotNull(second);
        Assert.True(second.Value > first.Value);
    }

    [Fact]
    public async Task GiveUp_deletes_in_progress_quest()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 1);
        await repo.AcceptQuestAsync(c, 2);

        await repo.GiveUpQuestAsync(c, 1);

        var active = Assert.Single(await repo.GetActiveQuestsAsync(c));
        Assert.Equal(2u, active.QuestId);
        Assert.Empty(await repo.GetCompletedQuestIdsAsync(c));
        Assert.Empty(await RawRowsAsync(c, 1)); 
    }

    [Fact]
    public async Task GiveUp_leaves_completed_run_alone()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 3);
        await repo.CompleteQuestAsync(c, 3);

        await repo.GiveUpQuestAsync(c, 3);

        Assert.Equal(new[] { 3u }, await repo.GetCompletedQuestIdsAsync(c));
        Assert.NotNull(await repo.GetLastCompletedAtAsync(c, 3));
    }

    [Fact]
    public async Task GiveUp_without_run_is_silent_noop()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.GiveUpQuestAsync(c, 77);
        Assert.Empty(await repo.GetActiveQuestsAsync(c));
    }

    [Fact]
    public async Task GiveUp_deletes_only_one_of_duplicate_runs()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 6);
        await repo.AcceptQuestAsync(c, 6);

        await repo.GiveUpQuestAsync(c, 6);

        Assert.Single(await repo.GetActiveQuestsAsync(c));
    }

    [Fact]
    public async Task Reaccept_after_give_up_starts_fresh_run()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 9);
        await repo.SaveProgressAsync(c, 9, "{\"kills\":3}");
        await repo.SetQuestsTracedAsync(c, new uint[] { 9 });
        await repo.GiveUpQuestAsync(c, 9);

        await repo.AcceptQuestAsync(c, 9);

        var q = Assert.Single(await repo.GetActiveQuestsAsync(c));
        Assert.Null(q.ProgressJson);
        Assert.False(q.IsTraced);
    }

    [Fact]
    public async Task SetQuestsTraced_pins_requested_and_unpins_rest()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 1);
        await repo.AcceptQuestAsync(c, 2);
        await repo.AcceptQuestAsync(c, 3);

        await repo.SetQuestsTracedAsync(c, new uint[] { 1, 3 });
        var traced = (await repo.GetActiveQuestsAsync(c)).ToDictionary(q => q.QuestId, q => q.IsTraced);
        Assert.True(traced[1]);
        Assert.False(traced[2]);
        Assert.True(traced[3]);

        
        await repo.SetQuestsTracedAsync(c, new uint[] { 2 });
        traced = (await repo.GetActiveQuestsAsync(c)).ToDictionary(q => q.QuestId, q => q.IsTraced);
        Assert.False(traced[1]);
        Assert.True(traced[2]);
        Assert.False(traced[3]);
    }

    [Fact]
    public async Task SetQuestsTraced_empty_set_unpins_everything()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 1);
        await repo.AcceptQuestAsync(c, 2);
        await repo.SetQuestsTracedAsync(c, new uint[] { 1, 2 });
        Assert.All(await repo.GetActiveQuestsAsync(c), q => Assert.True(q.IsTraced));

        await repo.SetQuestsTracedAsync(c, Array.Empty<uint>());

        Assert.All(await repo.GetActiveQuestsAsync(c), q => Assert.False(q.IsTraced));
    }

    [Fact]
    public async Task SetQuestsTraced_accepts_set_and_duplicate_inputs()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 1);
        await repo.AcceptQuestAsync(c, 2);

        await repo.SetQuestsTracedAsync(c, new HashSet<uint> { 2 });
        var traced = (await repo.GetActiveQuestsAsync(c)).ToDictionary(q => q.QuestId, q => q.IsTraced);
        Assert.False(traced[1]);
        Assert.True(traced[2]);

        await repo.SetQuestsTracedAsync(c, new uint[] { 1, 1, 1 });
        traced = (await repo.GetActiveQuestsAsync(c)).ToDictionary(q => q.QuestId, q => q.IsTraced);
        Assert.True(traced[1]);
        Assert.False(traced[2]);
    }

    [Fact]
    public async Task SetQuestsTraced_ignores_unknown_ids_completed_runs_and_other_characters()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var a = NewChar();
        var b = NewChar();
        await repo.AcceptQuestAsync(a, 1);
        await repo.AcceptQuestAsync(a, 2);
        await repo.AcceptQuestAsync(b, 1);
        await repo.SetQuestsTracedAsync(a, new uint[] { 2 });
        await repo.CompleteQuestAsync(a, 2); 

        await repo.SetQuestsTracedAsync(a, new uint[] { 1, 999 }); 

        var active = Assert.Single(await repo.GetActiveQuestsAsync(a));
        Assert.Equal(1u, active.QuestId);
        Assert.True(active.IsTraced);
        
        var completed = Assert.Single(await RawRowsAsync(a, 2));
        Assert.Equal((short)QuestStatus.Completed, completed.Status);
        Assert.True(completed.IsTraced);
        
        Assert.False(Assert.Single(await repo.GetActiveQuestsAsync(b)).IsTraced);
        Assert.Empty(await RawRowsAsync(a, 999));
    }

    [Fact]
    public async Task SaveProgress_stores_overwrites_and_clears()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 15);
        await repo.AcceptQuestAsync(c, 16);

        const string json = "{\"kills\":{\"1001\":3},\"note\":\"café \\\"x\\\"\"}";
        await repo.SaveProgressAsync(c, 15, json);
        var active = (await repo.GetActiveQuestsAsync(c)).ToDictionary(q => q.QuestId);
        Assert.Equal(json, active[15].ProgressJson); 
        Assert.Null(active[16].ProgressJson);

        await repo.SaveProgressAsync(c, 15, "{\"kills\":{\"1001\":4}}");
        active = (await repo.GetActiveQuestsAsync(c)).ToDictionary(q => q.QuestId);
        Assert.Equal("{\"kills\":{\"1001\":4}}", active[15].ProgressJson);

        await repo.SaveProgressAsync(c, 15, null);
        active = (await repo.GetActiveQuestsAsync(c)).ToDictionary(q => q.QuestId);
        Assert.Null(active[15].ProgressJson);
    }

    [Fact]
    public async Task SaveProgress_only_touches_in_progress_run()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 17);
        await repo.SaveProgressAsync(c, 17, "{\"a\":1}");
        await repo.CompleteQuestAsync(c, 17);

        await repo.SaveProgressAsync(c, 17, "{\"a\":2}"); 
        await repo.SaveProgressAsync(c, 18, "{\"b\":1}");  

        var row = Assert.Single(await RawRowsAsync(c, 17));
        Assert.Equal("{\"a\":1}", row.ProgressJson);
        Assert.Empty(await RawRowsAsync(c, 18));
    }

    [Fact]
    public async Task SaveProgress_does_not_affect_other_characters()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var a = NewChar();
        var b = NewChar();
        await repo.AcceptQuestAsync(a, 19);
        await repo.AcceptQuestAsync(b, 19);

        await repo.SaveProgressAsync(a, 19, "{\"only\":\"a\"}");

        Assert.Equal("{\"only\":\"a\"}", Assert.Single(await repo.GetActiveQuestsAsync(a)).ProgressJson);
        Assert.Null(Assert.Single(await repo.GetActiveQuestsAsync(b)).ProgressJson);
    }

    [Fact]
    public async Task Concurrent_accepts_get_distinct_ids()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();

        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => repo.AcceptQuestAsync(c, (uint)i)));

        var active = await repo.GetActiveQuestsAsync(c);
        Assert.Equal(20, active.Count);
        Assert.Equal(20, active.Select(q => q.Id).Distinct().Count());
    }

    [Fact]
    public async Task Concurrent_completes_of_one_run_complete_it_exactly_once()
    {
        if (!_fx.Available) return;
        var repo = new QuestRepository(_fx.Db!);
        var c = NewChar();
        await repo.AcceptQuestAsync(c, 50);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repo.CompleteQuestAsync(c, 50)));

        var row = Assert.Single(await RawRowsAsync(c, 50));
        Assert.Equal((short)QuestStatus.Completed, row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal(new[] { 50u }, await repo.GetCompletedQuestIdsAsync(c));
        Assert.Empty(await repo.GetActiveQuestsAsync(c));
    }
}
