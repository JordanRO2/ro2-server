using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// Publishes a character's current union membership to the host's in-process copy, which the character head
/// (2310) is built from. A join republishes the stored membership; a withdraw publishes none.
/// </summary>
public interface IUnionMembershipPublisher
{
    /// <summary>Publishes <paramref name="membership"/> for the character; null (or union 0) publishes no union.</summary>
    void Publish(uint characterId, UnionMembership? membership);
}
