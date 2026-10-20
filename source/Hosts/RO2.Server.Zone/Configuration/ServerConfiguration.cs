using RO2.Application.Configuration;

namespace RO2.Server.Zone.Configuration;

public class ServerConfiguration
{
    public ServerEndpoint Login { get; set; } = new() { Host = "0.0.0.0", Port = 7101 };
    public ServerEndpoint Zone { get; set; } = new() { Host = "0.0.0.0", Port = 7201 };
    public DatabaseConfig Database { get; set; } = new();
    public GameConfig Game { get; set; } = new();
    public WorldConfig World { get; set; } = new();
    public TickConfig Tick { get; set; } = new();
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
}
