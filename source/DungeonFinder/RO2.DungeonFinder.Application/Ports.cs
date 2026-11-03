using RO2.DungeonFinder.Domain;
using RO2.GameData.Tables;
using RO2.Inventory.Domain;

namespace RO2.DungeonFinder.Application;

/// <summary>One other online party member of a searcher.</summary>
public readonly record struct PartySeat(uint CharacterId, uint SessionId);

/// <summary>The searcher's party and its other online members.</summary>
public sealed record SearcherParty(uint PartyId, IReadOnlyList<PartySeat> OtherOnlineMembers);

/// <summary>Party read model. Called under the finder gate: implementations must be synchronous, non-blocking and non-reentrant.</summary>
public interface IFinderPartyView
{
    /// <summary>The searcher's party, or null when solo. Others are online and the same live character.</summary>
    SearcherParty? PartyOf(uint searcherCharacterId);

    /// <summary>True while the character is still in the world as that character and still in <paramref name="partyId"/>.</summary>
    bool IsStillMember(uint partyId, uint characterId);

    /// <summary>The character's party id, or null when solo.</summary>
    uint? PartyIdOf(uint characterId);
}

/// <summary>Whether a seat is still the live player on its session. Same locking rules as <see cref="IFinderPartyView"/>.</summary>
public interface ISeatLiveness
{
    /// <summary>True when the seat's character is still logged in on that session.</summary>
    bool IsLive(SeatKey seat);
}

/// <summary>The dungeon instance registry as the finder sees it.</summary>
public interface IInstanceBinding
{
    /// <summary>True while the character is bound to the instance.</summary>
    bool IsBound(long instanceId, uint characterId);

    /// <summary>Starts the cleared lifetime of the instance.</summary>
    void MarkCleared(long instanceId);
}

/// <summary>Find_Dungeon_List rows by list id.</summary>
public interface IFinderRowCatalog
{
    /// <summary>The row for <paramref name="listId"/>, or null when unknown.</summary>
    FindDungeonList? Row(uint listId);
}

/// <summary>Session-bound side effects of paying a finder run.</summary>
public interface IFinderRewardPublisher
{
    /// <summary>A reward item landed in the bag: quest objective, khara, then the 2333 pickup per stack.</summary>
    Task ItemLanded(SeatKey seat, uint itemId, int count, IReadOnlyList<InventoryItem> stacks);

    /// <summary>The run is paid: sends the 2816 completion notice, mailed when the bag was full.</summary>
    Task Completed(SeatKey seat, uint listId, bool mailed);
}
