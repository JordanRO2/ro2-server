namespace RO2.Server.Zone.Services;

/// <summary>Read-only view of a party for code outside the party feature, so it does not depend on <see cref="PartyState"/>'s fields.</summary>
public interface IPartyView
{
    uint PartyId { get; }

    /// <summary>Member key of the party leader.</summary>
    uint LeaderKey { get; }

    /// <summary>Snapshot of the member keys, in roster order; safe to iterate while the roster changes.</summary>
    IReadOnlyList<uint> MemberKeys { get; }

    byte ItemRule { get; }
    byte ItemQuality { get; }
    byte ExpRule { get; }
    byte PartyType { get; }

    /// <summary>The member's 1-based frame slot, or 0 when it holds none.</summary>
    byte FrameSlot(uint memberKey);

    bool IsLeader(uint memberKey);
}
