using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using System.Collections.Concurrent;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// In-process copy of union memberships, published for the 2310 character head.
/// Lets WorldEntryService write the union fields synchronously.
/// </summary>
/// <remarks>
/// The client keeps the union in the character record, and every character head write includes it.
/// Memberships are loaded at login and after a join. A withdraw clears the entry, so later head writes do not restore it.
/// </remarks>
public static class UnionMembershipStore
{
    private static readonly ConcurrentDictionary<uint, UnionMembership> Published = new();

 /// <summary>Loads the character's stored membership and publishes it, or publishes no union.</summary>
    public static async Task PreloadAsync(uint characterId, IUnionMembershipRepository memberships, CancellationToken ct = default)
        => Publish(characterId, await memberships.GetAsync(characterId, ct));

 /// <summary>Publishes a membership for the character. Null, or union 0, publishes no union.</summary>
    public static void Publish(uint characterId, UnionMembership? membership)
    {
        if (membership is { UnionId: not 0 } m) Published[characterId] = m;
        else Published.TryRemove(characterId, out _);
    }

 /// <summary>The published membership, or null when the character has no union.</summary>
    public static UnionMembership? PublishedFor(uint characterId) =>
        Published.TryGetValue(characterId, out var m) ? m : null;

 /// <summary>
 /// Writes the union fields of the 205-byte character head: [u32 union id][u64 join time, unix seconds],
 /// or [u32 0][FF x8] when the character has no union.
 /// </summary>
    public static void WriteHead(PacketWriter writer, uint characterId)
    {
        if (PublishedFor(characterId) is { } m)
        {
            writer.WriteUInt32(m.UnionId);
            writer.WriteUInt64((ulong)m.JoinedAtUnix);
        }
        else
        {
            writer.WriteUInt32(0);
            writer.WriteUInt64(ulong.MaxValue);
        }
    }
}
