using RO2.BuildingBlocks.Domain;

namespace RO2.Identity.Domain.Account;

/// <summary>
/// Value object describing an account's ban state. An account is banned when the ban flag is set and the ban
/// has no expiry or an expiry in the future.
/// </summary>
public sealed class BanPolicy : ValueObject
{
 /// <summary>The policy of an account that is not banned.</summary>
    public static readonly BanPolicy NotBanned = new(false, null, null);

 /// <summary>The banned flag.</summary>
    public bool IsBanned { get; }

 /// <summary>The ban expiry, or <see langword="null"/> for a permanent ban.</summary>
    public DateTime? BanExpires { get; }

 /// <summary>The ban reason, if any.</summary>
    public string? BanReason { get; }

 /// <summary>Creates a ban policy from stored fields.</summary>
 /// <param name="isBanned">The stored banned flag.</param>
 /// <param name="banExpires">The stored expiry, or <see langword="null"/> for permanent.</param>
 /// <param name="banReason">The stored reason, or <see langword="null"/>.</param>
    public BanPolicy(bool isBanned, DateTime? banExpires, string? banReason)
    {
        IsBanned = isBanned;
        BanExpires = banExpires;
        BanReason = banReason;
    }

 /// <summary>Whether the ban is active at the given instant.</summary>
 /// <param name="now">The current instant (UTC), supplied by the caller.</param>
 /// <returns><see langword="true"/> if login must be rejected for an active ban; otherwise <see langword="false"/>.</returns>
    public bool IsActiveAt(DateTime now) => IsBanned && (BanExpires == null || BanExpires > now);

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return IsBanned;
        yield return BanExpires;
        yield return BanReason;
    }
}
