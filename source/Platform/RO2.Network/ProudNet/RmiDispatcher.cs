using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;

namespace RO2.Network.ProudNet;

/// <summary>
/// Dispatches RMI packets to registered handlers
/// </summary>
public class RmiDispatcher
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ushort, PacketHandler> _handlers = new();
    private readonly ConcurrentDictionary<ushort, string> _opcodeNames = new();
    private readonly WarningThrottle _unhandledThrottle;
    private readonly WarningThrottle _faultThrottle;

    /// <summary>Most payload bytes an [UNHANDLED-RMI] line carries as hex; the rest is only counted.</summary>
    internal const int MaxLoggedPayloadBytes = 64;

    public delegate Task PacketHandler(ProudSession session, ReadOnlyMemory<byte> payload);

    public RmiDispatcher(ILogger logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _unhandledThrottle = new WarningThrottle(timeProvider);
        _faultThrottle = new WarningThrottle(timeProvider);
    }

    /// <summary>
    /// Register a packet handler for an opcode
    /// </summary>
    public void Register(ushort opcode, PacketHandler handler, string? name = null)
    {
        if (!_handlers.TryAdd(opcode, handler))
        {
            throw new InvalidOperationException($"Handler already registered for opcode 0x{opcode:X4}");
        }

        if (!string.IsNullOrEmpty(name))
        {
            _opcodeNames[opcode] = name;
        }

        _logger.LogDebug("Registered handler for opcode 0x{Opcode:X4} ({Name})", opcode, name ?? "unnamed");
    }

    /// <summary>
    /// Unregister a packet handler
    /// </summary>
    public bool Unregister(ushort opcode)
    {
        _opcodeNames.TryRemove(opcode, out _);
        return _handlers.TryRemove(opcode, out _);
    }

    /// <summary>
    /// Dispatch a packet to its handler
    /// </summary>
    public async Task DispatchAsync(ProudSession session, ushort opcode, ReadOnlyMemory<byte> payload)
    {
        if (_handlers.TryGetValue(opcode, out var handler))
        {
            try
            {
                await handler(session, payload);
            }
            catch (Exception ex)
            {
                var name = _opcodeNames.GetValueOrDefault(opcode, "unknown");
                if (WarningThrottle.IsMalformedPacket(ex))
                {
                    // A short or hostile body, one line and no stack trace: the peer controls how often
                    // this fires, so it must not be able to write a trace per packet.
                    if (_faultThrottle.TryAllow(opcode, out var suppressed))
                        _logger.LogWarning("Malformed packet 0x{Opcode:X4} ({Name}) for session {SessionId}: {Reason} ({Suppressed} similar suppressed)",
                            opcode, name, session.SessionId, ex.Message, suppressed);
                }
                else
                {
                    _logger.LogError(ex, "Error handling packet 0x{Opcode:X4} ({Name}) for session {SessionId}",
                        opcode, name, session.SessionId);
                }
            }
        }
        else if (_unhandledThrottle.TryAllow(opcode, out var suppressed))
        {
            // Catch-all for not-yet-implemented RMIs: log the id + the first bytes of the payload so unknown
            // RMIs can be found in the server logs. Grep "[UNHANDLED-RMI]".
            // The hex is capped (a peer picks the size, up to 64 KiB) and the line is rate-limited per opcode.
            var shown = payload.Span[..Math.Min(payload.Length, MaxLoggedPayloadBytes)];
            var data = shown.Length > 0 ? Convert.ToHexString(shown) : "";
            if (payload.Length > shown.Length)
                data += $"...(+{payload.Length - shown.Length} bytes)";
            _logger.LogWarning("[UNHANDLED-RMI] {Opcode} (0x{OpcodeHex:X4}) session={SessionId} len={Len} data={Data} suppressed={Suppressed}",
                opcode, opcode, session.SessionId, payload.Length, data, suppressed);
        }
    }

    /// <summary>
    /// Check if a handler is registered for an opcode
    /// </summary>
    public bool HasHandler(ushort opcode) => _handlers.ContainsKey(opcode);

    /// <summary>
    /// Get all registered opcodes
    /// </summary>
    public IEnumerable<ushort> GetRegisteredOpcodes() => _handlers.Keys;

    /// <summary>
    /// Get opcode name if registered
    /// </summary>
    public string? GetOpcodeName(ushort opcode)
    {
        return _opcodeNames.GetValueOrDefault(opcode);
    }
}

/// <summary>
/// Fixed-window limiter for log lines a peer can trigger at will (backlog S-H11): per key, the first
/// <c>perWindow</c> lines of each window pass and the rest are only counted; the count comes back with
/// the first line of the next window. Keys are small ints (opcodes, drop kinds), so the table is bounded.
/// </summary>
internal sealed class WarningThrottle(TimeProvider? timeProvider = null, int perWindow = 5, TimeSpan? window = null)
{
    private sealed class Slot
    {
        public long WindowStart;
        public int Count;
        public int Suppressed;
    }

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly long _windowTicks = (window ?? TimeSpan.FromSeconds(10)).Ticks;
    private readonly ConcurrentDictionary<int, Slot> _slots = new();

    /// <summary>True if this line may be logged; <paramref name="suppressed"/> is how many were dropped since the last one.</summary>
    public bool TryAllow(int key, out int suppressed)
    {
        var slot = _slots.GetOrAdd(key, static _ => new Slot { WindowStart = long.MinValue });
        var now = _time.GetUtcNow().UtcTicks;
        lock (slot)
        {
            suppressed = 0;
            if (slot.WindowStart == long.MinValue || now - slot.WindowStart >= _windowTicks)
            {
                suppressed = slot.Suppressed;
                slot.WindowStart = now;
                slot.Count = 0;
                slot.Suppressed = 0;
            }

            if (slot.Count < perWindow)
            {
                slot.Count++;
                return true;
            }

            slot.Suppressed++;
            suppressed = 0;
            return false;
        }
    }

    /// <summary>
    /// True for the exceptions a short or hostile packet body raises while a handler parses it
    /// (<see cref="PacketReader"/> underflow, bad length, out-of-range index): the peer's fault, not a
    /// server bug, so they log as one line instead of a stack trace.
    /// </summary>
    public static bool IsMalformedPacket(Exception ex) =>
        ex is InvalidOperationException { Message: PacketReader.EndOfBufferMessage }
            or ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException
            or InvalidDataException or FormatException or OverflowException;
}

/// <summary>
/// Extension methods for RmiDispatcher
/// </summary>
public static class RmiDispatcherExtensions
{
    /// <summary>
    /// Register a synchronous handler
    /// </summary>
    public static void RegisterSync(this RmiDispatcher dispatcher, ushort opcode, Action<ProudSession, ReadOnlyMemory<byte>> handler, string? name = null)
    {
        dispatcher.Register(opcode, (session, payload) =>
        {
            handler(session, payload);
            return Task.CompletedTask;
        }, name);
    }
}

/// <summary>
/// Attribute to mark packet handler methods
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class PacketHandlerAttribute(ushort opcode, string? name = null) : Attribute
{
    public ushort Opcode { get; } = opcode;
    public string? Name { get; } = name;
}
