using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;

namespace RO2.Network.Logging;

/// <summary>
/// Extension methods for creating logging scopes with session and user context.
/// </summary>
public static class LogContextExtensions
{
 /// <summary>
 /// Begins a logging scope with session context (session id and remote endpoint).
 /// </summary>
    public static IDisposable? BeginSessionScope(this ILogger logger, ProudSession session)
    {
        return logger.BeginScope(new Dictionary<string, object?>
        {
            ["SessionId"] = session.SessionId,
            ["RemoteEndPoint"] = session.RemoteEndPoint?.ToString()
        });
    }

 /// <summary>
 /// Begins a logging scope with user context (session id, username and account id). Call after the user has authenticated.
 /// </summary>
    public static IDisposable? BeginUserScope(this ILogger logger, ProudSession session)
    {
        return logger.BeginScope(new Dictionary<string, object?>
        {
            ["SessionId"] = session.SessionId,
            ["Username"] = session.Username,
            ["AccountId"] = session.AccountId
        });
    }

 /// <summary>
 /// Begins a logging scope with the given properties.
 /// </summary>
    public static IDisposable? BeginScope(this ILogger logger, params (string Key, object? Value)[] properties)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in properties)
        {
            dict[key] = value;
        }
        return logger.BeginScope(dict);
    }
}
