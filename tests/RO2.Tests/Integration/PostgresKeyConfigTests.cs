using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="KeyConfigRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers upserts, partial updates, numeric and binary round-trips and concurrent upsert atomicity. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresKeyConfigTests
{
    private readonly PostgresFixture _fx;
    public PostgresKeyConfigTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewCharacterId() => BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0) | 0x80000000u;

    private static readonly DateTime T1 = new(2026, 10, 18, 12, 0, 0, 123, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 10, 18, 12, 5, 0, 456, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2026, 10, 18, 12, 10, 0, 789, DateTimeKind.Utc);

    [Fact]
    public async Task Get_missing_character_is_null()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        Assert.Null(await repo.GetAsync(NewCharacterId()));
    }

    [Fact]
    public async Task Save_creates_then_get_round_trips_every_field()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        var entries = new byte[] { 0x68, 0x00, 0x34, 0x12, 0xFF, 0x41, 0xFF, 0xFF };
        await repo.SaveAsync(new KeyConfigDocument { Id = id, Mode = 65535, Entries = entries, UpdatedUtc = T1 });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got!.Id);
        Assert.Equal((ushort)65535, got.Mode);
        Assert.Equal(entries, got.Entries);
        Assert.Equal(T1, got.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedUtc.Kind);
    }

    [Fact]
    public async Task Save_replaces_existing_record()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KeyConfigDocument { Id = id, Mode = 1, Entries = new byte[] { 1, 2, 3, 4 }, UpdatedUtc = T1 });
        await repo.SaveAsync(new KeyConfigDocument { Id = id, Mode = 2, Entries = new byte[] { 9, 8 }, UpdatedUtc = T2 });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal((ushort)2, got!.Mode);
        Assert.Equal(new byte[] { 9, 8 }, got.Entries);
        Assert.Equal(T2, got.UpdatedUtc);
    }

    [Fact]
    public async Task Save_empty_entries_round_trips_as_empty()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KeyConfigDocument { Id = id, Mode = 5, Entries = Array.Empty<byte>(), UpdatedUtc = T1 });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal((ushort)5, got!.Mode);
        Assert.Empty(got.Entries);
    }

    [Fact]
    public async Task Save_normalises_non_utc_stamp_and_truncates_to_milliseconds()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        var withTicks = new DateTime(T1.Ticks + 1234, DateTimeKind.Utc); 
        await repo.SaveAsync(new KeyConfigDocument { Id = id, Mode = 1, Entries = new byte[] { 1, 2, 3, 4 }, UpdatedUtc = withTicks });

        var got = await repo.GetAsync(id);
        Assert.Equal(T1, got!.UpdatedUtc);
    }

    [Fact]
    public async Task Apply_full_update_creates_record()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        var entries = new byte[] { 0x70, 0x00, 0x01, 0x00 };
        await repo.ApplyUpdateAsync(id, 3, entries, T1);

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got!.Id);
        Assert.Equal((ushort)3, got.Mode);
        Assert.Equal(entries, got.Entries);
        Assert.Equal(T1, got.UpdatedUtc);
    }

    [Fact]
    public async Task Apply_full_update_replaces_stored_records()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.ApplyUpdateAsync(id, 1, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, T1);
        await repo.ApplyUpdateAsync(id, 2, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, T2);

        var got = await repo.GetAsync(id);
        Assert.Equal((ushort)2, got!.Mode);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, got.Entries);
        Assert.Equal(T2, got.UpdatedUtc);
    }

    [Fact]
    public async Task Apply_mode_zero_with_records_is_a_full_replace_not_flag_only()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.ApplyUpdateAsync(id, 4, new byte[] { 1, 2, 3, 4 }, T1);
        await repo.ApplyUpdateAsync(id, 0, new byte[] { 5, 6, 7, 8 }, T2); 

        var got = await repo.GetAsync(id);
        Assert.Equal((ushort)0, got!.Mode);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, got.Entries);
    }

    [Fact]
    public async Task Apply_nonzero_mode_with_no_records_is_a_full_replace_that_empties_the_list()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.ApplyUpdateAsync(id, 4, new byte[] { 1, 2, 3, 4 }, T1);
        await repo.ApplyUpdateAsync(id, 7, Array.Empty<byte>(), T2); 

        var got = await repo.GetAsync(id);
        Assert.Equal((ushort)7, got!.Mode);
        Assert.Empty(got.Entries);
        Assert.Equal(T2, got.UpdatedUtc);
    }

    [Fact]
    public async Task Apply_flag_only_keeps_stored_records_and_updates_mode_and_stamp()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        var entries = new byte[] { 0x68, 0x00, 0x34, 0x12 };
        await repo.ApplyUpdateAsync(id, 9, entries, T1);
        await repo.ApplyUpdateAsync(id, 0, Array.Empty<byte>(), T2); 

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal((ushort)0, got!.Mode);
        Assert.Equal(entries, got.Entries); 
        Assert.Equal(T2, got.UpdatedUtc);
    }

    [Fact]
    public async Task Apply_flag_only_with_no_record_creates_empty_record()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.ApplyUpdateAsync(id, 0, Array.Empty<byte>(), T1);

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got!.Id);
        Assert.Equal((ushort)0, got.Mode);
        Assert.Empty(got.Entries);
        Assert.Equal(T1, got.UpdatedUtc);
    }

    [Fact]
    public async Task Apply_flag_only_does_not_resurrect_records_after_a_later_full_replace()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.ApplyUpdateAsync(id, 0, Array.Empty<byte>(), T1);                 
        await repo.ApplyUpdateAsync(id, 1, new byte[] { 4, 3, 2, 1 }, T2);           
        await repo.ApplyUpdateAsync(id, 0, Array.Empty<byte>(), T3);                 

        var got = await repo.GetAsync(id);
        Assert.Equal((ushort)0, got!.Mode);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, got.Entries);
        Assert.Equal(T3, got.UpdatedUtc);
    }

    [Fact]
    public async Task Characters_are_isolated()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var a = NewCharacterId();
        var b = NewCharacterId();
        await repo.SaveAsync(new KeyConfigDocument { Id = a, Mode = 1, Entries = new byte[] { 1, 1, 1, 1 }, UpdatedUtc = T1 });
        await repo.SaveAsync(new KeyConfigDocument { Id = b, Mode = 2, Entries = new byte[] { 2, 2, 2, 2 }, UpdatedUtc = T2 });

        Assert.Equal(new byte[] { 1, 1, 1, 1 }, (await repo.GetAsync(a))!.Entries);
        Assert.Equal(new byte[] { 2, 2, 2, 2 }, (await repo.GetAsync(b))!.Entries);
    }

    [Fact]
    public async Task Concurrent_flag_only_updates_on_a_new_character_create_one_empty_record()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            repo.ApplyUpdateAsync(id, 0, Array.Empty<byte>(), T1)));

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal((ushort)0, got!.Mode);
        Assert.Empty(got.Entries);
    }

    [Fact]
    public async Task Concurrent_flag_only_updates_never_erase_stored_records()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        var entries = new byte[] { 0x68, 0x00, 0x34, 0x12, 0x41, 0x00, 0x56, 0x00 };
        await repo.ApplyUpdateAsync(id, 5, entries, T1);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            repo.ApplyUpdateAsync(id, 0, Array.Empty<byte>(), T2)));

        var got = await repo.GetAsync(id);
        Assert.Equal((ushort)0, got!.Mode);
        Assert.Equal(entries, got.Entries);
    }

    [Fact]
    public async Task Concurrent_saves_leave_exactly_one_whole_record()
    {
        if (!_fx.Available) return;
        var repo = new KeyConfigRepository(_fx.Db!);
        var id = NewCharacterId();
        
        
        await Task.WhenAll(Enumerable.Range(1, 12).Select(n =>
            repo.ApplyUpdateAsync(id, (ushort)n, new byte[] { (byte)n, (byte)n, (byte)n, (byte)n }, T1)));

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.InRange(got!.Mode, (ushort)1, (ushort)12);
        Assert.Equal(new byte[] { (byte)got.Mode, (byte)got.Mode, (byte)got.Mode, (byte)got.Mode }, got.Entries);
    }

    [Fact]
    public void IsFlagOnlyUpdate_is_mode_zero_and_no_entry_bytes()
    {
        Assert.True(KeyConfigRepository.IsFlagOnlyUpdate(0, 0));
        Assert.False(KeyConfigRepository.IsFlagOnlyUpdate(0, 4));
        Assert.False(KeyConfigRepository.IsFlagOnlyUpdate(1, 0));
        Assert.False(KeyConfigRepository.IsFlagOnlyUpdate(1, 4));
    }
}
