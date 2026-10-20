using System.Runtime.CompilerServices;
using Npgsql;
using RO2.BuildingBlocks.Domain;
using RO2.Identity.Domain.Account;

namespace RO2.Identity.Infrastructure.Postgres;

/// <summary>
/// PostgreSQL-backed <see cref="IAccountRepository"/> over the shared <c>accounts</c> table (the main server's
/// read-only AccountAccess reads the same table; this class owns the write side). Pure persistence, raw Npgsql.
/// Two intended differences from the Mongo provider: <see cref="NextIdAsync"/> draws from the atomic
/// <c>accounts_id_seq</c> sequence instead of the racy <c>max(Id)+1</c>, and username uniqueness is enforced
/// by the table's <c>UNIQUE (username)</c> constraint, so a duplicate <see cref="AddAsync"/> surfaces a
/// <see cref="PostgresException"/> (the race backstop behind the register flow's <see cref="UsernameExistsAsync"/> check).
/// </summary>
/// <remarks>
/// Hardened to match the Mongo provider (S-H9): usernames are unique case-insensitively (a unique index on
/// <c>lower(username)</c>, created lazily; lookups compare <c>lower(username)</c>), a unique violation on insert surfaces
/// as a <see cref="DomainException"/> rather than an uncaught <see cref="PostgresException"/>, and
/// <see cref="UpdateAsync"/> writes only the columns this aggregate changed since it was loaded, so a concurrent
/// PIN/last-login write is never overwritten by a stale whole-row update.
/// </remarks>
public sealed class PostgresAccountRepository : IAccountRepository
{
    private const string Columns =
        "id, username, password_hash, second_password_hash, email, created_at, last_login, " +
        "is_banned, ban_expires, ban_reason, access_level";

    /// <summary>Name of the case-insensitive unique index on <c>lower(username)</c>.</summary>
    public const string UsernameIndexName = "ux_accounts_username_lower";

    // The mutable state an aggregate had when this repository last loaded or saved it, so UpdateAsync can write
    // only what changed. An aggregate that never came through here has no entry and gets every mutable column.
    private static readonly ConditionalWeakTable<Account, AccountState> Baselines = new();

    private readonly IdentityPostgresContext _ctx;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexEnsured;

    /// <summary>Creates the repository over the given Identity Postgres context.</summary>
    /// <param name="ctx">The Identity Postgres context (owns the pooled data source).</param>
    public PostgresAccountRepository(IdentityPostgresContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
    }

    /// <summary>
    /// Whether the case-insensitive unique username index exists. <see langword="false"/> before
    /// <see cref="EnsureIndexesAsync"/> has run, or when the build was refused (existing case-variant duplicate
    /// usernames must be merged by the operator first); the table's exact-case <c>UNIQUE (username)</c> still holds.
    /// </summary>
    public bool UsernameUniquenessEnforced { get; private set; }

    /// <summary>
    /// Creates the unique <c>lower(username)</c> index (idempotent; the first <see cref="AddAsync"/> calls it lazily).
    /// Serialised across connections with an advisory lock so concurrent first calls cannot trip over each other.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when case-insensitive username uniqueness is enforced by the store.</returns>
    public async Task<bool> EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        if (_indexEnsured)
        {
            return UsernameUniquenessEnforced;
        }

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_indexEnsured)
            {
                return UsernameUniquenessEnforced;
            }

            try
            {
                await using var conn = await _ctx.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (var lockCmd = new NpgsqlCommand(
                    "SELECT pg_advisory_xact_lock(hashtext('ro2_identity_accounts_username_index'))", conn, tx))
                {
                    await lockCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var indexCmd = new NpgsqlCommand(
                    $"CREATE UNIQUE INDEX IF NOT EXISTS {UsernameIndexName} ON accounts (lower(username))", conn, tx))
                {
                    await indexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                UsernameUniquenessEnforced = true;
            }
            catch (PostgresException)
            {
                UsernameUniquenessEnforced = false;
            }

            _indexEnsured = true;
            return UsernameUniquenessEnforced;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken = default)
    {
        await using var conn = await _ctx.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM accounts WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)id.Value);
        return await ReadSingleAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Account?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var conn = await _ctx.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM accounts WHERE lower(username) = lower(@u) ORDER BY (username = @u) DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("u", username);
        return await ReadSingleAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var conn = await _ctx.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM accounts WHERE lower(username) = lower(@u) LIMIT 1", conn);
        cmd.Parameters.AddWithValue("u", username);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public async Task<AccountId> NextIdAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _ctx.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT nextval('accounts_id_seq')", conn);
        var value = (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new AccountId((uint)value);
    }

    /// <inheritdoc />
    /// <exception cref="DomainException">Thrown when the username (case-insensitively) or the id is already taken.</exception>
    public async Task AddAsync(Account aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await _ctx.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO accounts (id, username, password_hash, second_password_hash, email, created_at, last_login, " +
            "is_banned, ban_expires, ban_reason, access_level) " +
            "VALUES (@id, @u, @ph, @sph, @em, @ca, @ll, @ib, @be, @br, @al)",
            conn);
        Bind(cmd, aggregate);
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DomainException($"account {aggregate.Id.Value} was rejected: username or id already exists");
        }

        Baselines.AddOrUpdate(aggregate, AccountState.Of(aggregate));
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Account aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var current = AccountState.Of(aggregate);
        Baselines.TryGetValue(aggregate, out var baseline);

        await using var cmd = new NpgsqlCommand();
        var sets = new List<string>();
        void Set(string column, string parameter, object? value)
        {
            sets.Add($"{column} = @{parameter}");
            cmd.Parameters.AddWithValue(parameter, value ?? DBNull.Value);
        }

        if (baseline is null || baseline.PasswordHash != current.PasswordHash)
        {
            Set("password_hash", "ph", current.PasswordHash);
        }

        if (baseline is null || baseline.SecondPasswordHash != current.SecondPasswordHash)
        {
            Set("second_password_hash", "sph", current.SecondPasswordHash);
        }

        if (baseline is null || baseline.Email != current.Email)
        {
            Set("email", "em", current.Email ?? string.Empty);
        }

        if (baseline is null || baseline.LastLogin != current.LastLogin)
        {
            Set("last_login", "ll", current.LastLogin is { } ll ? ToUtc(ll) : null);
        }

        if (baseline is null || baseline.IsBanned != current.IsBanned)
        {
            Set("is_banned", "ib", current.IsBanned);
        }

        if (baseline is null || baseline.BanExpires != current.BanExpires)
        {
            Set("ban_expires", "be", current.BanExpires is { } be ? ToUtc(be) : null);
        }

        if (baseline is null || baseline.BanReason != current.BanReason)
        {
            Set("ban_reason", "br", current.BanReason);
        }

        if (baseline is null || baseline.AccessLevel != current.AccessLevel)
        {
            Set("access_level", "al", (short)current.AccessLevel);
        }

        if (sets.Count > 0)
        {
            await using var conn = await _ctx.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            cmd.Connection = conn;
            cmd.CommandText = $"UPDATE accounts SET {string.Join(", ", sets)} WHERE id = @id";
            cmd.Parameters.AddWithValue("id", (long)aggregate.Id.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Baselines.AddOrUpdate(aggregate, current);
    }

    /// <summary>The mutable fields of an account (username and creation time never change after registration).</summary>
    private sealed record AccountState(
        string PasswordHash,
        string? SecondPasswordHash,
        string? Email,
        DateTime? LastLogin,
        bool IsBanned,
        DateTime? BanExpires,
        string? BanReason,
        byte AccessLevel)
    {
        public static AccountState Of(Account a) => new(
            a.PasswordHash, a.SecondPasswordHash, a.Email.Value, a.LastLogin,
            a.Ban.IsBanned, a.Ban.BanExpires, a.Ban.BanReason, a.AccessLevel);
    }

    private static void Bind(NpgsqlCommand cmd, Account a)
    {
        cmd.Parameters.AddWithValue("id", (long)a.Id.Value);
        cmd.Parameters.AddWithValue("u", a.Username);
        cmd.Parameters.AddWithValue("ph", a.PasswordHash);
        cmd.Parameters.AddWithValue("sph", (object?)a.SecondPasswordHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("em", a.Email.Value ?? string.Empty);
        cmd.Parameters.AddWithValue("ca", ToUtc(a.CreatedAt));
        cmd.Parameters.AddWithValue("ll", a.LastLogin is { } ll ? ToUtc(ll) : DBNull.Value);
        cmd.Parameters.AddWithValue("ib", a.Ban.IsBanned);
        cmd.Parameters.AddWithValue("be", a.Ban.BanExpires is { } be ? ToUtc(be) : DBNull.Value);
        cmd.Parameters.AddWithValue("br", (object?)a.Ban.BanReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("al", (short)a.AccessLevel);
    }

    private static async Task<Account?> ReadSingleAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var account = Account.Rehydrate(
            new AccountId((uint)r.GetInt64(0)),
            r.GetString(1),
            r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            Email.FromStorage(r.GetString(4)),
            new BanPolicy(
                r.GetBoolean(7),
                r.IsDBNull(8) ? null : ToUtc(r.GetDateTime(8)),
                r.IsDBNull(9) ? null : r.GetString(9)),
            (byte)r.GetInt16(10),
            ToUtc(r.GetDateTime(5)),
            r.IsDBNull(6) ? null : ToUtc(r.GetDateTime(6)));
        Baselines.AddOrUpdate(account, AccountState.Of(account));
        return account;
    }

    // Npgsql writes timestamptz only from Kind=Utc values; normalise whatever the aggregate carries.
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
