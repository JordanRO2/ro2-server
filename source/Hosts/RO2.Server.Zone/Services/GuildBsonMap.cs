using System.Runtime.CompilerServices;
using MongoDB.Bson.Serialization;
using RO2.Guilds.Domain;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Mongo class map of the <see cref="Guild"/> aggregate. The Domain type carries no BSON attributes, so the map
/// states what they used to: <c>Id</c> is the document <c>_id</c>, the in-memory <c>Gate</c> lock is never stored,
/// and unknown stored fields are ignored. Every other member maps by its own name, as before.
/// </summary>
/// <remarks>Moves to the Guild Mongo infrastructure project when the repository does.</remarks>
public static class GuildBsonMap
{
    private static readonly Lock s_gate = new();
    private static bool s_registered;

    // Registers our Guild class map at Zone-assembly load, before any code can serialize a Guild and trigger a
    // default auto-map (which would keep the Gate lock and map Id, not _id). Must win the race, hence eager.
    [ModuleInitializer]
    internal static void Init() => Register();

    /// <summary>Registers the map once; safe to call more than once and after a default map may already exist.</summary>
    public static void Register()
    {
        lock (s_gate)
        {
            if (s_registered) return;
            s_registered = true;
            // Another map may already be registered if a Guild was serialized before this ran. Do not re-register
            // (RegisterClassMap throws on a duplicate key); the eager ModuleInitializer makes ours the one that lands.
            if (BsonClassMap.IsClassMapRegistered(typeof(Guild))) return;
            RuntimeHelpers.RunClassConstructor(typeof(MongoDbContext).TypeHandle);
            BsonClassMap.RegisterClassMap<Guild>(cm =>
            {
                cm.AutoMap();
                cm.SetIgnoreExtraElements(true);
                cm.MapIdMember(g => g.Id);
                cm.UnmapMember(g => g.Gate);
            });
        }
    }
}
