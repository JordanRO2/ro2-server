using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.ProudNet;

namespace RO2.Network.Handlers;

/// <summary>
/// Base class for ProudNet core protocol handlers.
/// These handlers manage connection lifecycle (handshake, encryption, heartbeats).
/// Core handlers are typically stateless and work directly with the session.
/// </summary>
public abstract class CoreHandlerBase<THandler> : ICoreHandler where THandler : class
{
    protected readonly ILogger<THandler> Logger;

 /// <inheritdoc />
    public abstract ProudCoreOpCode Opcode { get; }

 /// <inheritdoc />
    public abstract string Name { get; }

    protected CoreHandlerBase(ILogger<THandler> logger)
    {
        Logger = logger;
    }

 /// <inheritdoc />
    public async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        HandlerLog.Handling(Logger, session.SessionId, Name, payload.Length);

        try
        {
            await HandleCoreAsync(session, payload, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {SessionId}: Error handling {Name}", session.SessionId, Name);
            throw;
        }
    }

 /// <summary>
 /// Handle the core protocol message.
 /// Override this method to implement the handler logic.
 /// </summary>
 /// <param name="session">The session that received the message</param>
 /// <param name="payload">The message payload</param>
 /// <param name="ct">Cancellation token</param>
    protected abstract Task HandleCoreAsync(ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct);

 /// <summary>
 /// Create a PacketReader for the payload
 /// </summary>
    protected static PacketReader CreateReader(ReadOnlyMemory<byte> payload) => new(payload.Span);
}

/// <summary>
/// The per-packet "Handling" line shared by <see cref="CoreHandlerBase{THandler}"/>,
/// <see cref="MultiCoreHandlerBase{THandler}"/> and <see cref="RmiHandlerBase{THandler}"/>,
/// source-generated so that it checks the level before touching its arguments and never boxes the
/// session id or size (LogDebug built a params object[] and boxed both on every packet, even with
/// Debug off; the heartbeats (<see cref="ProudCoreOpCode.UnreliablePing"/> and <see cref="ProudCoreOpCode.SpeedHackDetectorPing"/>) are the most frequent frames). Same
/// template, level and property names as the LogDebug call. It lives outside the generic bases so
/// the one definition serves all three; the category is still the handler's own, because the
/// handler's logger is passed in.
/// </summary>
internal static partial class HandlerLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: Handling {Name} ({Size} bytes)")]
    internal static partial void Handling(ILogger logger, uint sessionId, string name, int size);
}

/// <summary>
/// Base class for handlers that process multiple core opcodes.
/// Used for heartbeat handlers that handle both <see cref="ProudCoreOpCode.UnreliablePing"/> and <see cref="ProudCoreOpCode.SpeedHackDetectorPing"/>.
/// </summary>
public abstract class MultiCoreHandlerBase<THandler> : IMultiCoreHandler where THandler : class
{
    protected readonly ILogger<THandler> Logger;

 /// <inheritdoc />
    public abstract IEnumerable<ProudCoreOpCode> Opcodes { get; }

 /// <inheritdoc />
    public abstract string Name { get; }

    protected MultiCoreHandlerBase(ILogger<THandler> logger)
    {
        Logger = logger;
    }

 /// <inheritdoc />
    public async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        HandlerLog.Handling(Logger, session.SessionId, Name, payload.Length);

        try
        {
            await HandleCoreAsync(session, payload, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {SessionId}: Error handling {Name}", session.SessionId, Name);
            throw;
        }
    }

 /// <summary>
 /// Handle the core protocol message.
 /// Override this method to implement the handler logic.
 /// </summary>
 /// <param name="session">The session that received the message</param>
 /// <param name="payload">The message payload</param>
 /// <param name="ct">Cancellation token</param>
    protected abstract Task HandleCoreAsync(ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct);

 /// <summary>
 /// Create a PacketReader for the payload
 /// </summary>
    protected static PacketReader CreateReader(ReadOnlyMemory<byte> payload) => new(payload.Span);
}

/// <summary>
/// Base class for Login server core handlers
/// </summary>
public abstract class LoginCoreHandlerBase<THandler> : CoreHandlerBase<THandler>, ILoginCoreHandler
    where THandler : class
{
    protected LoginCoreHandlerBase(ILogger<THandler> logger) : base(logger)
    {
    }
}

/// <summary>
/// Base class for Login server multi-core handlers
/// </summary>
public abstract class LoginMultiCoreHandlerBase<THandler> : MultiCoreHandlerBase<THandler>, ILoginMultiCoreHandler
    where THandler : class
{
    protected LoginMultiCoreHandlerBase(ILogger<THandler> logger) : base(logger)
    {
    }
}

/// <summary>
/// Base class for Zone server core handlers
/// </summary>
public abstract class ZoneCoreHandlerBase<THandler> : CoreHandlerBase<THandler>, IZoneCoreHandler
    where THandler : class
{
    protected ZoneCoreHandlerBase(ILogger<THandler> logger) : base(logger)
    {
    }
}

/// <summary>
/// Base class for Zone server multi-core handlers
/// </summary>
public abstract class ZoneMultiCoreHandlerBase<THandler> : MultiCoreHandlerBase<THandler>, IZoneMultiCoreHandler
    where THandler : class
{
    protected ZoneMultiCoreHandlerBase(ILogger<THandler> logger) : base(logger)
    {
    }
}

/// <summary>
/// Base class for Chat server core handlers
/// </summary>
public abstract class ChatCoreHandlerBase<THandler> : CoreHandlerBase<THandler>, IChatCoreHandler
    where THandler : class
{
    protected ChatCoreHandlerBase(ILogger<THandler> logger) : base(logger)
    {
    }
}

/// <summary>
/// Base class for Chat server multi-core handlers
/// </summary>
public abstract class ChatMultiCoreHandlerBase<THandler> : MultiCoreHandlerBase<THandler>, IChatMultiCoreHandler
    where THandler : class
{
    protected ChatMultiCoreHandlerBase(ILogger<THandler> logger) : base(logger)
    {
    }
}
