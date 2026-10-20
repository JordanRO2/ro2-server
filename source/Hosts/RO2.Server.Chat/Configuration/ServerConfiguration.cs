using RO2.Application.Configuration;

namespace RO2.Server.Chat.Configuration;

public class ServerConfiguration
{
    public ServerEndpoint Chat { get; set; } = new() { Host = "0.0.0.0", Port = 7401 };
    public DatabaseConfig Database { get; set; } = new();
}
