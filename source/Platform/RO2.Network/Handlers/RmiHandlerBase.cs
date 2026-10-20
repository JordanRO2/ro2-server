using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.ProudNet;

namespace RO2.Network.Handlers;

/// <summary>
/// Base class for RMI packet handlers with DI support.
/// Handlers are singletons, but create scoped service providers per request.
/// </summary>
public abstract class RmiHandlerBase<THandler> : IRmiHandler where THandler : class
{
    protected readonly IServiceScopeFactory ScopeFactory;
    protected readonly ILogger<THandler> Logger;

    // A malformed body is the peer's doing and repeats at its pace: the stack trace is replaced by one
    // throttled line (per handler, since the throttle is per instance and handlers are singletons).
    private readonly WarningThrottle _malformedThrottle = new();

    /// <inheritdoc />
    public abstract ushort Opcode { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    protected RmiHandlerBase(IServiceScopeFactory scopeFactory, ILogger<THandler> logger)
    {
        ScopeFactory = scopeFactory;
        Logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        // Every RMI passes through here: source-generated, level-checked, no boxing (HandlerLog).
        HandlerLog.Handling(Logger, session.SessionId, Name, payload.Length);

        try
        {
            using var scope = ScopeFactory.CreateScope();
            await HandleAsync(session, payload, scope.ServiceProvider, ct);
        }
        catch (Exception ex)
        {
            if (!WarningThrottle.IsMalformedPacket(ex))
                Logger.LogError(ex, "Session {SessionId}: Error handling {Name}", session.SessionId, Name);
            else if (_malformedThrottle.TryAllow(0, out var suppressed))
                Logger.LogWarning("Session {SessionId}: Malformed {Name} packet ({Size} bytes): {Reason} ({Suppressed} similar suppressed)",
                    session.SessionId, Name, payload.Length, ex.Message, suppressed);
            throw;
        }
    }

    /// <summary>
    /// Handle the packet with access to scoped services.
    /// Override this method to implement the handler logic.
    /// </summary>
    /// <param name="session">The session that received the packet</param>
    /// <param name="payload">The packet payload</param>
    /// <param name="services">Scoped service provider for this request</param>
    /// <param name="ct">Cancellation token</param>
    protected abstract Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct);

    /// <summary>
    /// Create a PacketReader for the payload
    /// </summary>
    protected static PacketReader CreateReader(ReadOnlyMemory<byte> payload) => new(payload.Span);
}

/// <summary>
/// Base class for Login server RMI handlers
/// </summary>
public abstract class LoginRmiHandlerBase<THandler> : RmiHandlerBase<THandler>, ILoginRmiHandler
    where THandler : class
{
    protected LoginRmiHandlerBase(IServiceScopeFactory scopeFactory, ILogger<THandler> logger)
        : base(scopeFactory, logger)
    {
    }
}

/// <summary>
/// Base class for Zone server RMI handlers
/// </summary>
public abstract class ZoneRmiHandlerBase<THandler> : RmiHandlerBase<THandler>, IZoneRmiHandler
    where THandler : class
{
    protected ZoneRmiHandlerBase(IServiceScopeFactory scopeFactory, ILogger<THandler> logger)
        : base(scopeFactory, logger)
    {
    }
}

/// <summary>
/// Base class for Chat server RMI handlers
/// </summary>
public abstract class ChatRmiHandlerBase<THandler> : RmiHandlerBase<THandler>, IChatRmiHandler
    where THandler : class
{
    protected ChatRmiHandlerBase(IServiceScopeFactory scopeFactory, ILogger<THandler> logger)
        : base(scopeFactory, logger)
    {
    }
}
