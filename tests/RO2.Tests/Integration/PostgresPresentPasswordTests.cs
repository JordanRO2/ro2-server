using System.Security.Cryptography;
using System.Text;
using Npgsql;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="PresentPasswordRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers has/verify, first set, change with the old password, invalid passwords, salted hash storage and per-account isolation. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresPresentPasswordTests
{
    private readonly PostgresFixture _fx;
    public PostgresPresentPasswordTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewAccount() => (uint)Random.Shared.NextInt64(3_000_000_000L, uint.MaxValue);

    private async Task<(string Hash, DateTime UpdatedAt)?> ReadRowAsync(uint accountId)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT hash, updated_at FROM present_passwords WHERE account_id = @a", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetString(0), reader.GetDateTime(1));
    }

    [Fact]
    public async Task Unknown_account_has_no_password_and_verify_is_false()
    {
        if (!_fx.Available) return;
        var repo = new PresentPasswordRepository(_fx.Db!);
        var acct = NewAccount();
        Assert.False(await repo.HasPasswordAsync(acct));
        Assert.False(await repo.VerifyAsync(acct, "1234"));
        Assert.Null(await ReadRowAsync(acct));
    }

    [Fact]
    public async Task First_set_ignores_old_password_then_has_and_verify()
    {
        if (!_fx.Available) return;
        var repo = new PresentPasswordRepository(_fx.Db!);
        var acct = NewAccount();
        
        Assert.True(await repo.SetAsync(acct, "garbage", "1234"));
        Assert.True(await repo.HasPasswordAsync(acct));
        Assert.True(await repo.VerifyAsync(acct, "1234"));
        Assert.False(await repo.VerifyAsync(acct, "4321"));
        Assert.False(await repo.VerifyAsync(acct, ""));
    }

    [Fact]
    public async Task Change_with_correct_old_password_replaces_it()
    {
        if (!_fx.Available) return;
        var repo = new PresentPasswordRepository(_fx.Db!);
        var acct = NewAccount();
        Assert.True(await repo.SetAsync(acct, "", "1111"));
        Assert.True(await repo.SetAsync(acct, "1111", "2222"));
        Assert.True(await repo.VerifyAsync(acct, "2222"));
        Assert.False(await repo.VerifyAsync(acct, "1111")); 
    }

    [Fact]
    public async Task Change_with_wrong_old_password_is_refused_and_changes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new PresentPasswordRepository(_fx.Db!);
        var acct = NewAccount();
        Assert.True(await repo.SetAsync(acct, "", "1111"));
        var before = await ReadRowAsync(acct);

        Assert.False(await repo.SetAsync(acct, "9999", "2222"));
        Assert.False(await repo.SetAsync(acct, "", "2222")); 

        Assert.True(await repo.VerifyAsync(acct, "1111"));
        Assert.False(await repo.VerifyAsync(acct, "2222"));
        var after = await ReadRowAsync(acct);
        Assert.Equal(before, after); 
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("12a4")]
    [InlineData("12 4")]
    [InlineData("-123")]
    public async Task Invalid_new_password_is_refused_on_first_set(string bad)
    {
        if (!_fx.Available) return;
        var repo = new PresentPasswordRepository(_fx.Db!);
        var acct = NewAccount();
        Assert.False(await repo.SetAsync(acct, "", bad));
        Assert.False(await repo.HasPasswordAsync(acct)); 
        Assert.Null(await ReadRowAsync(acct));
    }

    [Fact]
    public async Task Invalid_new_password_does_not_change_an_existing_one()
    {
        if (!_fx.Available) return;
        var repo = new PresentPasswordRepository(_fx.Db!);
        var acct = NewAccount();
        Assert.True(await repo.SetAsync(acct, "", "1111"));
        Assert.False(await repo.SetAsync(acct, "1111", "12x4")); 
        Assert.True(await repo.VerifyAsync(acct, "1111"));
    }

    [Fact]
    public async Task Passwords_are_per_account()
    {
        if (!_fx.Available) return;
        var repo = new PresentPasswordRepository(_fx.Db!);
        var a = NewAccount();
        var b = a == uint.MaxValue ? a - 1 : a + 1;
        Assert.True(await repo.SetAsync(a, "", "1234"));
        Assert.True(await repo.HasPasswordAsync(a));
        Assert.False(await repo.HasPasswordAsync(b));
        Assert.False(await repo.VerifyAsync(b, "1234"));

        
        Assert.True(await repo.SetAsync(b, "", "1234"));
        Assert.True(await repo.VerifyAsync(b, "1234"));
        Assert.NotEqual((await ReadRowAsync(a))!.Value.Hash, (await ReadRowAsync(b))!.Value.Hash);
    }

    [Fact]
    public async Task Stored_value_is_the_salted_sha256_not_the_password()
    {
        if (!_fx.Available) return;
        var repo = new PresentPasswordRepository(_fx.Db!);
        var acct = NewAccount();
        var before = DateTime.UtcNow.AddSeconds(-5);
        Assert.True(await repo.SetAsync(acct, "", "0420"));

        var row = await ReadRowAsync(acct);
        Assert.NotNull(row);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{acct}:0420")));
        Assert.Equal(expected, row!.Value.Hash);
        Assert.DoesNotContain("0420", row.Value.Hash.Replace(expected, ""));
        Assert.True(row.Value.UpdatedAt >= before && row.Value.UpdatedAt <= DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Set_upserts_one_row_and_refreshes_updated_at()
    {
        if (!_fx.Available) return;
        var repo = new PresentPasswordRepository(_fx.Db!);
        var acct = NewAccount();
        Assert.True(await repo.SetAsync(acct, "", "1111"));
        var first = await ReadRowAsync(acct);
        await Task.Delay(50);
        Assert.True(await repo.SetAsync(acct, "1111", "2222"));
        var second = await ReadRowAsync(acct);

        Assert.NotEqual(first!.Value.Hash, second!.Value.Hash);
        Assert.True(second.Value.UpdatedAt > first.Value.UpdatedAt);

        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM present_passwords WHERE account_id = @a", conn);
        cmd.Parameters.AddWithValue("a", (long)acct);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Setting_the_same_password_again_with_it_as_old_succeeds()
    {
        if (!_fx.Available) return;
        var repo = new PresentPasswordRepository(_fx.Db!);
        var acct = NewAccount();
        Assert.True(await repo.SetAsync(acct, "", "5555"));
        Assert.True(await repo.SetAsync(acct, "5555", "5555"));
        Assert.True(await repo.VerifyAsync(acct, "5555"));
    }

    [Fact]
    public void IsValidPassword_accepts_only_four_digits()
    {
        Assert.True(PresentPasswordRepository.IsValidPassword("0000"));
        Assert.True(PresentPasswordRepository.IsValidPassword("9876"));
        Assert.False(PresentPasswordRepository.IsValidPassword(null));
        Assert.False(PresentPasswordRepository.IsValidPassword("123"));
        Assert.False(PresentPasswordRepository.IsValidPassword("12345"));
        Assert.False(PresentPasswordRepository.IsValidPassword("abcd"));
    }
}
