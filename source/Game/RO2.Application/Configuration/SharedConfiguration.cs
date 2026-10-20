namespace RO2.Application.Configuration;

public class ServerEndpoint
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; }
}

public class DatabaseConfig
{
    public string Provider { get; set; } = "Mongo";
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName { get; set; } = "ro2_server";

 /// <summary>Redis endpoint for the non-durable cross-process signal bus between Zone and Chat (close-range
 /// signals). Used when <see cref="Provider"/> pairs the relational store with Redis; ignored by the all-Mongo
 /// deployment, which uses a MongoDB capped collection instead.</summary>
    public string RedisConnectionString { get; set; } = "localhost:6379";
}

public class WorldConfig
{
    public string ServerName { get; set; } = "Bugnarok";
    public int ChannelCount { get; set; } = 3;
    public int ServerIndex { get; set; } = 1;
    public int ServerInstance { get; set; } = 12;
}
