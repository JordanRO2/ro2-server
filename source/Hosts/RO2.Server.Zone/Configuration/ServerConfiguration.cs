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

    /// <summary>Guardian-seal (RMI 1694 imprint) roll tables. EMPTY by default so no invented balance ships: a seal
    /// rolls nothing until an operator fills the per-Seal_Reinforce_ID candidate lines. The retail numbers are not in
    /// any data table or the capture dump (the roll is server-authoritative and no sealed-item record was captured),
    /// so they must be measured from a fresh seal capture or supplied here.</summary>
    public SealConfig Seal { get; set; } = new();

    /// <summary>Custom per-class base primary stats, keyed by job id, each <c>[STR, AGI, INT, WIS, VIT, LUK]</c> (LUK
    /// optional, defaults 1). EMPTY by default, which means every class uses the measured retail base of flat 1 for all
    /// primaries (the Standard_Stat columns are legacy and ignored). An operator adds a row here to make a class start
    /// with a custom base — this is the hook for custom-content class bases; leaving it empty keeps retail behaviour.</summary>
    public Dictionary<uint, int[]> ClassBaseStats { get; set; } = new();
}

/// <summary>
/// Guardian-seal roll config. Keyed by SealItem_Info.Seal_Reinforce_ID; each entry lists the candidate stat lines a
/// seal of that group may roll. A roll picks one candidate uniformly, then a value uniformly in [MinValue, MaxValue]
/// (MinValue may be negative). Independent of the accessory (same table for a level-1 or an ML40 item). EMPTY default.
/// </summary>
public class SealConfig
{
    /// <summary>Per Seal_Reinforce_ID (e.g. 100001/100002/200001/200002) → its candidate roll lines. Defaults are the
    /// operator-provided retail caps (2026-11-05): named guardians roll any of STR 66 / AGI 51 / INT 66 / WIS 51 /
    /// VIT 60 / AP 233 / MP 980; the "Guardian" seal rolls AP/MP only. The 100000 group can roll negative (range
    /// [-cap, cap]); the 200000 "Cheny" group is non-negative ([0, cap]). All flat (Kind 1). The range is symmetric:
    /// whatever a stat can reach positive it can reach negative. Trait types: STR 0, AGI(DEX) 1, INT 2, WIS(WIL) 3,
    /// VIT 4, AP (Physical Attack) 23, MP (Magic Attack) 25.
    /// Retail's full seal pool is 13 stats (confirmed from capture samples, 2026-11-05): the 7 above plus Max HP 21,
    /// Max SP 22, Precision/hit 58, Evade/dodge 59, Critical 60, Guard/block 61. Those 6 are not in the default table;
    /// an operator adds them here with caps (none shipped, so no invented balance).</summary>
    public Dictionary<uint, List<SealRollLine>> ReinforceRolls { get; set; } = BuildDefaults();

    // Normal ("named") seals roll the PRIMARY stats only (STR..VIT). The Seal of Guardian rolls the 8 advanced stats:
    // AP 23, MP 25, Max HP 21, Max SP 22, hit 58, dodge 59, crit 60, parry 61. Caps for AP/MP are operator-given; the
    // other six are retail-sample maxima (small n) as a placeholder until the operator sets real caps.
    private static readonly (byte Type, int Cap)[] NamedCaps =
        { (0, 66), (1, 51), (2, 66), (3, 51), (4, 60) };
    private static readonly (byte Type, int Cap)[] GuardianCaps =
        { (21, 221), (22, 385), (23, 233), (25, 980), (58, 73), (59, 122), (60, 93), (61, 82) };

    private static Dictionary<uint, List<SealRollLine>> BuildDefaults()
    {
        static List<SealRollLine> Lines((byte Type, int Cap)[] caps, bool negative) =>
            caps.Select(c => new SealRollLine { Kind = 1, TraitType = c.Type, MinValue = negative ? -c.Cap : 0, MaxValue = c.Cap }).ToList();
        return new Dictionary<uint, List<SealRollLine>>
        {
            [100001] = Lines(NamedCaps, negative: true),
            [200001] = Lines(NamedCaps, negative: false),
            [100002] = Lines(GuardianCaps, negative: true),
            [200002] = Lines(GuardianCaps, negative: false),
        };
    }
}

/// <summary>One candidate a seal may roll: a trait <see cref="Kind"/> (1 flat / 2 percent), the <see cref="TraitType"/>
/// (TraitInfo.Trait_Type), and the inclusive signed value range [<see cref="MinValue"/>, <see cref="MaxValue"/>].</summary>
public class SealRollLine
{
    public byte Kind { get; set; } = 1;
    public byte TraitType { get; set; }
    public int MinValue { get; set; }
    public int MaxValue { get; set; }
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
