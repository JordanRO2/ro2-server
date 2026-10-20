using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;

namespace RO2.Tests;

/// <summary>Memory-only guild emblem registry for tests. It is never initialized with InitAsync, so no database connection is made.</summary>
internal static class TestEmblems
{
    public static IGuildEmblemRepository Create()
        => new GuildEmblemRepository(new MongoDbContext("mongodb://127.0.0.1:1"));
}
