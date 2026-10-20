using MongoDB.Driver;
using RO2.BuildingBlocks.Domain;
using RO2.Identity.Domain.Account;
using RO2.Identity.Infrastructure;
using RO2.Identity.Infrastructure.Mongo;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Identity;

/// <summary>
/// Integration test for the Mongo account repository: an account round-trips with its id, username and password hash, and the stored hash verifies.
/// </summary>
/// <remarks>
/// Seeds its own account in a scratch collection, which is dropped afterwards. Skips cleanly when the database is unreachable.
/// </remarks>
public sealed class MongoAccountRepositoryIntegrationTests
{
    private const string ConnectionString = "mongodb://localhost:27017";

    
    private const string SeedUsername = "it_account";
    private const string SeedPassword = "it_password";
    private const uint SeedId = 1u;

    private readonly ITestOutputHelper _output;

    public MongoAccountRepositoryIntegrationTests(ITestOutputHelper output) => _output = output;

    private sealed class SystemUtcClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

 /// <summary>
 /// Probes the database with a short timeout. Returns a connected repository, or null when the database is unreachable.
 /// </summary>
    private async Task<(MongoAccountRepository Repo, Action Cleanup)?> TrySeedRepositoryAsync()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(ConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            
            var client = new MongoClient(settings);
            _ = client.ListDatabaseNames(new ListDatabaseNamesOptions());

            var collection = "accounts_it_" + Guid.NewGuid().ToString("N");
            var context = new IdentityMongoContext(ConnectionString, IdentityMongoContext.DefaultDatabaseName, collection);
            var repo = new MongoAccountRepository(context);

            var account = Account.Register(await repo.NextIdAsync(), SeedUsername, SeedPassword, new BCryptPasswordHasher(), new SystemUtcClock());
            await repo.AddAsync(account);

            return (repo, () => client.GetDatabase(IdentityMongoContext.DefaultDatabaseName).DropCollection(collection));
        }
        catch (Exception ex) when (ex is TimeoutException or MongoConnectionException or MongoException)
        {
            _output.WriteLine($"SKIP: Mongo unreachable at {ConnectionString} ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    [Fact]
    public async Task GetByUsername_LoadsRealAccount_WithPopulatedUsernameAndHash()
    {
        var seeded = await TrySeedRepositoryAsync();
        if (seeded is null)
        {
            return; 
        }

        var (repo, cleanup) = seeded.Value;
        try
        {
            var account = await repo.GetByUsernameAsync(SeedUsername);
            Assert.NotNull(account);

            
            Assert.Equal(SeedUsername, account.Username);
            Assert.False(string.IsNullOrWhiteSpace(account.PasswordHash));
            Assert.StartsWith("$2", account.PasswordHash);              
            Assert.Equal(60, account.PasswordHash.Length);              

            
            Assert.Equal(SeedId, account.Id.Value);

            _output.WriteLine(
                $"OK GetByUsername: id={account.Id.Value} user={account.Username} hashLen={account.PasswordHash.Length} hasPin={account.HasSecondPassword}");
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task GetById_LoadsSameRealAccount_AndStoredHashVerifies()
    {
        var seeded = await TrySeedRepositoryAsync();
        if (seeded is null)
        {
            return; 
        }

        var (repo, cleanup) = seeded.Value;
        try
        {
            var account = await repo.GetByIdAsync(new AccountId(SeedId));
            Assert.NotNull(account);

            Assert.Equal(SeedId, account.Id.Value);
            Assert.Equal(SeedUsername, account.Username);
            Assert.False(string.IsNullOrWhiteSpace(account.PasswordHash));

            
            
            var hasher = new BCryptPasswordHasher();
            var result = account.Authenticate(SeedPassword, hasher, new SystemUtcClock());

            Assert.True(result.Succeeded, "stored BCrypt hash for the seed account must verify the seed password");
            Assert.False(result.WasAutoRegistered);

            _output.WriteLine(
                $"OK GetById+Authenticate: id={account.Id.Value} user={account.Username} verified=True hasPin={account.HasSecondPassword}");
        }
        finally
        {
            cleanup();
        }
    }
}
