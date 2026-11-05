using RO2.Application.Configuration;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Configuration;

public class ServerConfiguration
{
    public ServerEndpoint Login { get; set; } = new() { Host = "0.0.0.0", Port = 7101 };
    public ServerEndpoint Zone { get; set; } = new() { Host = "0.0.0.0", Port = 7201 };
    public DatabaseConfig Database { get; set; } = new();
    public GameConfig Game { get; set; } = new();
    public WorldConfig World { get; set; } = new();
    public TickConfig Tick { get; set; } = new();
    public ColosseumConfig Colosseum { get; set; } = new();
}

/// <summary>
/// Simulation tick (game loop) settings. The loop is a variable-rate fixed timestep: the rate ranges
/// between MinHz (idle) and MaxHz (active) and degrades toward MinHz under sustained overload.
/// This is not the persistence rate.
/// </summary>
public class TickConfig
{
    public int MinHz { get; set; } = 20;    
    public int MaxHz { get; set; } = 128;   
    public bool Adaptive { get; set; } = true;   
    public int MaxCatchUpSteps { get; set; } = 5; 
}

/// <summary>
/// Colosseum opening schedule. OpenHoursUtc are the server-clock hours at which the arena opens (one-hour windows);
/// confirmed correct by the operator, and in no data table. MinAnnounceLevel is the lowest level told on world entry
/// that it is open.
/// </summary>
public class ColosseumConfig
{
    public int[] OpenHoursUtc { get; set; } = { 1, 13, 18 };
    public byte MinAnnounceLevel { get; set; } = 2;
}

public class GameConfig
{
    public double ExpRate { get; set; } = 1.0;
    public double DropRate { get; set; } = 1.0;
    public double GoldRate { get; set; } = 1.0;
    public int ClientInitDelayMs { get; set; } = 500;

    /// <summary>GM /zeny default grant when the command is given no amount, in internal gold.</summary>
    public long GmDefaultZenyAmount { get; set; } = 10_000_000;

    /// <summary>Largest quantity one GM /i command grants; a typo must not mint an unbounded pile.</summary>
    public int GmMaxGiveQuantity { get; set; } = 9999;

    /// <summary>Largest number of stacks one GM /i command emits.</summary>
    public int GmMaxGiveRecords { get; set; } = 200;

    /// <summary>Fraction of an auction-house sale the seller keeps; the house cut is (1 − this). The retail
    /// default is 0.95 (a 5% cut). The proceeds are computed in single precision, so the default is byte-exact.</summary>
    public double AuctionSellerProceedsRate { get; set; } = 0.95;

    /// <summary>Cap in seconds on the elapsed time one client move may claim, so idle time cannot be banked for a
    /// speed-hack jump. A move may cover at most speed * (cap + 0.5 s). Raise it if legitimate clients that lag longer
    /// than that get their moves refused. Must be positive.</summary>
    public float MoveMaxElapsedSeconds { get; set; } = MovementValidator.DefaultMaxElapsedSeconds;

    /// <summary>Master-level (job-mastery) tunables; the defaults are the retail values.</summary>
    public MasteryConfig Mastery { get; set; } = new();
}

/// <summary>
/// Job-mastery / Master-level settings. The defaults match retail (the data tables and the two Dapara point items);
/// an operator can retune the balance here without touching code.
/// </summary>
public class MasteryConfig
{
    /// <summary>ItemInfo id of the Master Level Stat Point item (Dapara store): a one-time bonus-stat-point grant.</summary>
    public uint StatPointItemId { get; set; } = 30000314;

    /// <summary>Bonus stat points the stat item grants.</summary>
    public int StatPointGrant { get; set; } = 100;

    /// <summary>ItemInfo id of the Master Level Skill Point item (Dapara store): a one-time talent-point grant.</summary>
    public uint SkillPointItemId { get; set; } = 30000313;

    /// <summary>Talent points the skill item grants.</summary>
    public int SkillPointGrant { get; set; } = 10;

    // Note: the raw/displayed point scale (1000) is NOT here - it is a WIRE fact (the 2986 total is raw = displayed *
    // 1000, and the client shows total / 1000), not a balance tunable, so it stays the single MasteryRules.PointScale
    // constant used by every cost and cap path.

    /// <summary>Per-stat bonus allocation cap for a character without a Master level.</summary>
    public int BaseStatAllocationCap { get; set; } = 50;

    /// <summary>Per-stat bonus allocation cap once a character has a Master level (the whole pool may go into one stat).</summary>
    public int MasterStatAllocationCap { get; set; } = 999;

    /// <summary>Base level a character must reach before it can convert to Master level 1.</summary>
    public byte BaseLevelToStart { get; set; } = 50;
}
