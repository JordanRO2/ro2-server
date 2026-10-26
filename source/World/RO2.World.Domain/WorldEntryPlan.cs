namespace RO2.Worlds.Domain;

/// <summary>How a player enters a map: the first entry after login, or a cross-map warp for a player already in the world.</summary>
public enum WorldEntryMode
{
    /// <summary>The first entry after login (RMI 1417).</summary>
    Login,
    /// <summary>A cross-map warp: the burst without the login-only messages.</summary>
    Warp,
}

/// <summary>
/// One message of a world-entry or departure sequence. The step names the message; the host owns its opcode and builds its body.
/// </summary>
public enum WorldEntryStep
{
    /// <summary>The key config (2558).</summary>
    AccountConfig,
    /// <summary>The dependent dungeon info (2743).</summary>
    DependentDungeonInfo,
    /// <summary>The trait initialization (2595); carries the HP and SP of the trait message after it.</summary>
    PcInitTrait,
    /// <summary>The character trait sheet (2498) sent with the burst.</summary>
    PcTrait,
    /// <summary>The server time (2669).</summary>
    ServerTime,
    /// <summary>The craft index (2535); the host sends it only to a novice with a table to send.</summary>
    CraftIndex,
    /// <summary>The character detail (2310).</summary>
    CharacterDetail,
    /// <summary>The second trait sheet (2498) a login sends after the character detail.</summary>
    LoginPcTrait,
    /// <summary>The learned skill list (2488); the host sends it only when the list is not empty.</summary>
    UseSkillList,
    /// <summary>The card book charge time (2793); the host sends it only when the book is charged.</summary>
    CardBookCharge,
    /// <summary>The empty ranking top list (2982).</summary>
    RankingTopList,
    /// <summary>The map load (2311).</summary>
    MapLoad,
    /// <summary>The quick-slot bar (2472).</summary>
    QuickSlots,
    /// <summary>The empty vehicle quick-slot list (2523).</summary>
    VehicleQuickSlot,
    /// <summary>The owned vehicles (2522).</summary>
    AllVehicleList,
    /// <summary>The monster book count (2913).</summary>
    MonsterCollection,
    /// <summary>The pet book count (2919).</summary>
    PetCollection,
    /// <summary>The active pet answer (2918).</summary>
    ActivePet,
    /// <summary>The card book registration of each held MVP card (2588); zero or more messages.</summary>
    RegisterCardBook,
    /// <summary>The actor move speed (2497).</summary>
    MoveSpeed,
    /// <summary>The zeny balance (2435).</summary>
    ZenyBalance,
    /// <summary>The departure marker (2938).</summary>
    DepartureMarker,
    /// <summary>The self despawn (2505), listing the mover as its own subject.</summary>
    SelfDespawn,
}

/// <summary>
/// The ordered messages of one way to enter a map. Holds the order and the choice of steps only: the bodies, the opcodes,
/// the conditions on optional steps and the sends belong to the host.
/// </summary>
public sealed class WorldEntryPlan
{
    private static readonly WorldEntryStep[] PreMapLoadSteps =
    [
        WorldEntryStep.AccountConfig,
        WorldEntryStep.DependentDungeonInfo,
        WorldEntryStep.PcInitTrait,
        WorldEntryStep.PcTrait,
        WorldEntryStep.ServerTime,
    ];

    private static readonly WorldEntryStep[] DepartureSteps =
    [
        WorldEntryStep.DepartureMarker,
        WorldEntryStep.SelfDespawn,
    ];

    private static readonly WorldEntryStep[] InPlaceMapLoadSteps =
    [
        WorldEntryStep.MapLoad,
        WorldEntryStep.QuickSlots,
        WorldEntryStep.VehicleQuickSlot,
        WorldEntryStep.AllVehicleList,
    ];

    private static readonly WorldEntryPlan LoginPlan = new(WorldEntryMode.Login,
    [
        ..PreMapLoadSteps,
        WorldEntryStep.CraftIndex,
        WorldEntryStep.CharacterDetail,
        WorldEntryStep.LoginPcTrait,
        WorldEntryStep.UseSkillList,
        WorldEntryStep.CardBookCharge,
        WorldEntryStep.RankingTopList,
        WorldEntryStep.MapLoad,
        WorldEntryStep.QuickSlots,
        WorldEntryStep.VehicleQuickSlot,
        WorldEntryStep.AllVehicleList,
        WorldEntryStep.MonsterCollection,
        WorldEntryStep.PetCollection,
        WorldEntryStep.ActivePet,
        WorldEntryStep.RegisterCardBook,
        WorldEntryStep.MoveSpeed,
        WorldEntryStep.ZenyBalance,
    ]);

    private static readonly WorldEntryPlan WarpPlan = new(WorldEntryMode.Warp,
    [
        ..PreMapLoadSteps,
        WorldEntryStep.MapLoad,
        WorldEntryStep.QuickSlots,
        WorldEntryStep.VehicleQuickSlot,
        WorldEntryStep.AllVehicleList,
        WorldEntryStep.MonsterCollection,
        WorldEntryStep.PetCollection,
        WorldEntryStep.ActivePet,
        WorldEntryStep.RegisterCardBook,
    ]);

    private WorldEntryPlan(WorldEntryMode mode, WorldEntryStep[] steps)
    {
        Mode = mode;
        Steps = steps;
    }

    /// <summary>The way of entering this plan describes.</summary>
    public WorldEntryMode Mode { get; }

    /// <summary>Every step of the entry, in send order, starting with <see cref="PreMapLoad"/>.</summary>
    public IReadOnlyList<WorldEntryStep> Steps { get; }

    /// <summary>The steps after <see cref="PreMapLoad"/>, in send order.</summary>
    public IReadOnlyList<WorldEntryStep> AfterPreMapLoad => Steps.Skip(PreMapLoadSteps.Length).ToArray();

    /// <summary>The messages that precede the map load, the same for both modes: key config, dependent dungeon info, trait initialization, trait sheet and server time.</summary>
    public static IReadOnlyList<WorldEntryStep> PreMapLoad => PreMapLoadSteps;

    /// <summary>The two departure messages of a cross-map warp, in order: the departure marker and the self despawn.</summary>
    public static IReadOnlyList<WorldEntryStep> Departure => DepartureSteps;

    /// <summary>The map-load messages of a move in place on the current map, in order: the map load, the quick-slot bar and the two vehicle lists.</summary>
    public static IReadOnlyList<WorldEntryStep> InPlaceMapLoad => InPlaceMapLoadSteps;

    /// <summary>The plan for <paramref name="mode"/>.</summary>
    public static WorldEntryPlan For(WorldEntryMode mode) => mode switch
    {
        WorldEntryMode.Login => LoginPlan,
        WorldEntryMode.Warp => WarpPlan,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "no world-entry plan for this mode"),
    };
}
