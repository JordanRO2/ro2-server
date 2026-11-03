using RO2.DungeonFinder.Domain;
using RO2.Inventory.Domain;

namespace RO2.DungeonFinder.Application;

/// <summary>One other online party member of a searcher.</summary>
public readonly record struct PartySeat(uint CharacterId, uint SessionId, long Incarnation);

/// <summary>The searcher's party and its other online members.</summary>
public sealed record SearcherParty(uint PartyId, IReadOnlyList<PartySeat> OtherOnlineMembers);

/// <summary>Party read model. Called under the finder gate: implementations must be synchronous, non-blocking and non-reentrant.</summary>
public interface IFinderPartyView
{
    /// <summary>The searcher's party, or null when solo. Others are online and the same live character.</summary>
    SearcherParty? PartyOf(uint searcherCharacterId);

    /// <summary>True while the seat's session is still in <paramref name="partyId"/>.</summary>
    bool IsStillMember(uint partyId, SeatKey seat);
}

/// <summary>Whether a seat is still the live player on its session. Same locking rules as <see cref="IFinderPartyView"/>.</summary>
public interface ISeatLiveness
{
    /// <summary>True when the seat's character is still logged in on that session.</summary>
    bool IsLive(SeatKey seat);
}

/// <summary>Session-bound side effects of paying a finder run, resolved by the paid character.</summary>
public interface IFinderRewardPublisher
{
    /// <summary>A reward item landed in the bag: quest objective, khara, then the 2333 pickup per stack.</summary>
    Task ItemLanded(uint characterId, uint itemId, int count, IReadOnlyList<InventoryItem> landedStacks);

    /// <summary>The run is paid: sends the 2816 completion notice in the given state.</summary>
    Task Completed(uint characterId, uint listId, CompleteGiftState state);
}
