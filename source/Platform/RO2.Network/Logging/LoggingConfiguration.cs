using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace RO2.Network.Logging;

/// <summary>
/// Serilog logging configuration for RO2 servers.
/// Provides unified logging to both console and file with daily rotation.
/// </summary>
public static class LoggingConfiguration
{
    /// <summary>
    /// Configure Serilog logging for a server with dual output (console + file).
    /// </summary>
    /// <param name="builder">The host builder</param>
    /// <param name="serverName">Server name (e.g., "Login", "Zone")</param>
    /// <param name="logDirectory">Directory for log files (default: "logs")</param>
    /// <returns>The configured host builder</returns>
    public static IHostBuilder UseServerLogging(
        this IHostBuilder builder,
        string serverName,
        string logDirectory = "logs")
    {
        
        
        return builder
            .ConfigureLogging(logging => logging.ClearProviders())
            .UseSerilog((context, services, configuration) =>
        {
            var logFile = Path.Combine(logDirectory, $"{serverName.ToLower()}_server.log");

            
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            configuration
                
                .MinimumLevel.Debug()
                
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                
                .Enrich.WithProperty("ServerType", serverName)
                
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                    theme: AnsiConsoleTheme.Code)
                
                .WriteTo.File(
                    path: logFile,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                    shared: true);
        });
    }

    /// <summary>
    /// Write a startup banner to both console and log file.
    /// </summary>
    public static void WriteStartupBanner(string serverName)
    {
        
        
        var banner = $"""
            ===========================================
              Ragnarok Online 2 - {serverName} Server
              .NET {Environment.Version} / C# 15
            ===========================================
            """;

        Log.Information(banner);
    }
}
