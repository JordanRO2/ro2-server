namespace RO2.Parties.Domain;

/// <summary>An open party invitation, stored by the host keyed by the target.</summary>
public sealed record PartyInvite(ulong InviterGid, string RequestedName, DateTime CreatedAt, uint InviterPartyId)
{
    /// <summary>How long an open invitation blocks a second one for the same target.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public bool IsOpen(DateTime nowUtc) => nowUtc - CreatedAt < Lifetime;
}
