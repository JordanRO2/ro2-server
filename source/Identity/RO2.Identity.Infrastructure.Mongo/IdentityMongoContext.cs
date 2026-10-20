using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace RO2.Identity.Infrastructure.Mongo;

/// <summary>
/// MongoDB context for the Identity service. Owns the connection to the account store and exposes the
/// <c>accounts</c> collection.
/// </summary>
/// <remarks>
/// The database and collection names are constructor parameters, so a move to a dedicated database is a
/// configuration change. Extra document elements that this service does not model are ignored on deserialization.
/// </remarks>
public sealed class IdentityMongoContext
{
 /// <summary>The default account database name.</summary>
    public const string DefaultDatabaseName = "ro2_server";

 /// <summary>The accounts collection name.</summary>
    public const string AccountsCollectionName = "accounts";

    // Namespace prefix that scopes the ignore-extra-elements convention to this service's own document types.
    private const string IdentityNamespacePrefix = "RO2.Identity";

    private const string IgnoreExtraElementsConventionName = IdentityNamespacePrefix + ".IgnoreExtraElements";

    private static readonly object ConventionGate = new();
    private static bool _conventionsRegistered;

    private readonly IMongoDatabase _database;
    private readonly string _accountsCollectionName;

 /// <summary>Creates the context for the given connection string, database and collection.</summary>
 /// <param name="connectionString">The MongoDB connection string.</param>
 /// <param name="databaseName">The database name. Defaults to <see cref="DefaultDatabaseName"/>.</param>
 /// <param name="accountsCollectionName">The accounts collection name. Defaults to <see cref="AccountsCollectionName"/>.</param>
    public IdentityMongoContext(
        string connectionString,
        string databaseName = DefaultDatabaseName,
        string accountsCollectionName = AccountsCollectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountsCollectionName);

        RegisterConventions();
        var client = new MongoClient(connectionString);
        _database = client.GetDatabase(databaseName);
        _accountsCollectionName = accountsCollectionName;
    }

 /// <summary>The accounts collection.</summary>
    public IMongoCollection<AccountDocument> Accounts =>
        _database.GetCollection<AccountDocument>(_accountsCollectionName);

    private static void RegisterConventions()
    {
        if (_conventionsRegistered)
        {
            return;
        }

        lock (ConventionGate)
        {
            if (_conventionsRegistered)
            {
                return;
            }

            
            ConventionRegistry.Register(
                IgnoreExtraElementsConventionName,
                new ConventionPack { new IgnoreExtraElementsConvention(true) },
                type => type.Namespace?.StartsWith(IdentityNamespacePrefix, StringComparison.Ordinal) == true);

            _conventionsRegistered = true;
        }
    }
}
