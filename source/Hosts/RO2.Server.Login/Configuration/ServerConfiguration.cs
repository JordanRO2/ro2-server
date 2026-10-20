using RO2.Application.Configuration;

namespace RO2.Server.Login.Configuration;

public class ServerConfiguration
{
    public ServerEndpoint Login { get; set; } = new() { Host = "0.0.0.0", Port = 7101 };
    public ServerEndpoint Zone { get; set; } = new() { Host = "0.0.0.0", Port = 7201 };
    public DatabaseConfig Database { get; set; } = new();
    public GameConfig Game { get; set; } = new();
    public WorldConfig World { get; set; } = new();
}

public class GameConfig
{
    public double ExpRate { get; set; } = 1.0;
    public double DropRate { get; set; } = 1.0;
    public double GoldRate { get; set; } = 1.0;
    public int ClientInitDelayMs { get; set; } = 500;

    /// <summary>
    /// Accepted client version for the 12001 VersionCheck, as the client's own version display reads it:
    /// <c>word.b0.b1.b2.b3</c> — the u32 at request body+8 followed by the four config bytes at body+12. The
    /// shipped client shows <c>1.1.7.2.37.26390</c>; only the first five fields (1.1.7.2.37) are the checked
    /// fingerprint, the trailing build number travels in an appended tag that is not matched. On a client UPDATE,
    /// read the new client's version string and set this, then restart — no code change needed.
    /// </summary>
    public string ClientVersion { get; set; } = "1.1.7.2.37";

    /// <summary>Lifetime in minutes of the Zone hand-off token issued with the 12504 SelectServer answer. The
    /// client turns around and connects to the Zone immediately, so this only has to outlast that round trip.</summary>
    public int ZoneTokenLifetimeMinutes { get; set; } = 5;
}
