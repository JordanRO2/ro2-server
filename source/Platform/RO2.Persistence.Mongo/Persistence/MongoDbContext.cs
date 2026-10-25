using MongoDB.Driver;
using MongoDB.Bson.Serialization.Conventions;
using RO2.Domain.Entities;

namespace RO2.Persistence.Mongo.Persistence;

/// <summary>
/// Process-wide handle on the MongoDB database shared by the Login, Zone and Chat servers. Hands out typed
/// collections by name; the stores in this namespace own their collection names.
/// </summary>
public class MongoDbContext
{
    /// <summary>Database the servers use when the connection config names none.</summary>
    public const string DefaultDatabaseName = "ro2_server";

    /// <summary>Collection holding the player <see cref="Character"/> documents.</summary>

    private readonly IMongoDatabase _database;

    static MongoDbContext()
    {
        // Schema-drift safety: a server still running an older version of an entity must not fail when it
        // deserializes a document that carries newer fields (for example a Character field added after that
        // server was built). IgnoreExtraElements makes unknown document fields a no-op for every mapped type.
        ConventionRegistry.Register(
            "IgnoreExtraElements",
            new ConventionPack { new IgnoreExtraElementsConvention(true) },
            _ => true);
    }

    public MongoDbContext(string connectionString, string databaseName = DefaultDatabaseName)
    {
        var client = new MongoClient(connectionString);
        _database = client.GetDatabase(databaseName);
    }


    public IMongoCollection<T> GetCollection<T>(string name) => _database.GetCollection<T>(name);
}
