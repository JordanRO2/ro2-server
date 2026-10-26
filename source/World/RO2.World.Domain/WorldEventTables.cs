using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.Worlds.Domain;

/// <summary>The three world-event tables the stage rules read.</summary>
public sealed record WorldEventTables(
    IReadOnlyList<WorldEventMain> Main,
    IReadOnlyList<WorldEventCheckGroup> Checks,
    IReadOnlyList<WorldEventEventGroup> Groups)
{
 /// <summary>Check type of the rows that require offering an item to an NPC.</summary>
    public const byte CheckTypeOffering = 2;

 /// <summary>The tables as loaded in the game data store.</summary>
    public static WorldEventTables From(GameDataStore store)
        => new(store.WorldEventMain, store.WorldEventCheckGroup, store.WorldEventEventGroup);

 /// <summary>The main-table row of an event at the given stage.</summary>
    public WorldEventMain? Stage(uint eventId, byte stage)
        => Main.FirstOrDefault(r => r.World_Event_ID == eventId && r.Stage_Level == stage);

 /// <summary>The offering rows of the stage's first check group, in index order. Empty when the group id is 0.</summary>
    public IReadOnlyList<WorldEventCheckGroup> OfferingRows(WorldEventMain stage)
        => stage.Check_Group_ID_1 == 0
            ? Array.Empty<WorldEventCheckGroup>()
            : Checks.Where(c => c.Check_Group_ID == stage.Check_Group_ID_1 && c.Check_Type == CheckTypeOffering)
                .OrderBy(c => c.INDEX).ToList();

 /// <summary>The event-group rows of the stage, in table order.</summary>
    public IReadOnlyList<WorldEventEventGroup> EventRows(WorldEventMain stage)
        => Groups.Where(g => g.Event_Group_ID == stage.Event_Group_ID).ToList();
}
