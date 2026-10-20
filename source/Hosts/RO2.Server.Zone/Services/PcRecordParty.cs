using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Party handle for PC records. Static record writers cannot reach the party service, so each party service
/// registers a resolver here.
/// </summary>
internal static class PcRecordParty
{
    private static readonly ConditionalWeakTable<object, Func<PlayerState, uint?>> Resolvers = new();

 /// <summary>Registers the resolver of <paramref name="owner"/>, which returns the party handle of a PC, or null.</summary>
    internal static void Register(object owner, Func<PlayerState, uint?> resolver) => Resolvers.AddOrUpdate(owner, resolver);

 /// <summary>The party handle of <paramref name="player"/>, or null when it is in no party.</summary>
    internal static uint? HandleOf(PlayerState player)
    {
        foreach (var (_, resolve) in Resolvers)
            if (resolve(player) is { } handle)
                return handle;
        return null;
    }
}
