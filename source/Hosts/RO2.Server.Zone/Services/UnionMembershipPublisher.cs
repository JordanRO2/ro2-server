using RO2.Characters.Application;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="IUnionMembershipPublisher"/> with the in-process <see cref="UnionMembershipStore"/>.</summary>
internal sealed class UnionMembershipPublisher : IUnionMembershipPublisher
{
    public void Publish(uint characterId, UnionMembership? membership)
        => UnionMembershipStore.Publish(characterId, membership);
}
