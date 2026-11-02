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

    /// <summary>Registers the map once; safe to call more than once.</summary>
    public static void Register()
    {
        lock (s_gate)
        {
            if (s_registered) return;
            RuntimeHelpers.RunClassConstructor(typeof(MongoDbContext).TypeHandle);
            BsonClassMap.RegisterClassMap<Guild>(cm =>
            {
                cm.AutoMap();
                cm.SetIgnoreExtraElements(true);
                cm.MapIdMember(g => g.Id);
                cm.UnmapMember(g => g.Gate);
            });
            s_registered = true;
        }
    }
}
