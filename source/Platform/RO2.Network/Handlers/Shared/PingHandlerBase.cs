using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;

namespace RO2.Network.Handlers.Shared;

/// <summary>
/// No-op base for the reliable ping slot (<see cref="ProudCoreOpCode.S2CRoutedMulticast2"/>). Kept so the login, zone and chat reliable-ping handlers share a common base. The reliable ping itself is an RMI, <see cref="Protocol.ProudNetC2SOpcodes.ReliablePing"/>.
/// </summary>
public abstract class ReliablePingHandlerBase<THandler> : CoreHandlerBase<THandler>
    where THandler : class
{
    public override ProudCoreOpCode Opcode => ProudCoreOpCode.S2CRoutedMulticast2;
    public override string Name => "ReliablePing";

    protected ReliablePingHandlerBase(ILogger<THandler> logger) : base(logger) { }

    protected override Task HandleCoreAsync(
        ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// Answers the client's unreliable ping (<see cref="ProudCoreOpCode.UnreliablePing"/>, 0x1B) with an unreliable pong (<see cref="UnreliablePongMessage"/>).
/// </summary>
/// <remarks>
/// The ping body is a u64 client time followed by a u32 smoothed ping. This is a single-opcode handler, so it takes precedence over multi-opcode handlers that also list 0x1B. The login, zone and chat unreliable-ping handlers derive from it.
/// </remarks>
public abstract class UnreliablePingHandlerBase<THandler> : CoreHandlerBase<THandler>
    where THandler : class
{
    public override ProudCoreOpCode Opcode => ProudCoreOpCode.UnreliablePing;
    public override string Name => "UnreliablePing";

    protected UnreliablePingHandlerBase(ILogger<THandler> logger) : base(logger) { }

    protected override Task HandleCoreAsync(
        ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var pong = UnreliablePongMessage.Build(payload.Span, UnreliablePongMessage.ServerTimeMs());
        
        return pong is null
            ? Task.CompletedTask
            : session.SendCoreMessageAsync(ProudCoreOpCode.UnreliablePong, pong, ct);
    }
}

/// <summary>
/// Builds the body of the unreliable pong (<see cref="ProudCoreOpCode.UnreliablePong"/>, 0x1D).
/// </summary>
/// <remarks>
/// The body is the echoed client time (u64) followed by the server time in milliseconds (u64).
/// </remarks>
public static class UnreliablePongMessage
{
 /// <summary>Size in bytes of the client time (u64), the first field of both the ping and the pong body.</summary>
    private const int ClientTimeBytes = sizeof(ulong);

 /// <summary>Size in bytes of the smoothed ping (u32), the second field of the ping body.</summary>
    private const int SmoothedPingBytes = sizeof(uint);

 /// <summary>Size in bytes of the server time (u64), the second field of the pong body.</summary>
    private const int ServerTimeBytes = sizeof(ulong);

 /// <summary>Milliseconds per second, for scaling the monotonic timer.</summary>
    private const long MillisecondsPerSecond = 1000;

 /// <summary>Size in bytes of the ping body: u64 client time and u32 smoothed ping.</summary>
    public const int PingBodyBytes = ClientTimeBytes + SmoothedPingBytes;

 /// <summary>Size in bytes of the pong body: u64 echoed client time and u64 server time.</summary>
    public const int BodyBytes = ClientTimeBytes + ServerTimeBytes;

 /// <summary>
 /// Builds the pong body for a ping body. Returns null when the ping body is shorter than <see cref="PingBodyBytes"/>; bytes beyond that length are ignored.
 /// </summary>
    public static byte[]? Build(ReadOnlySpan<byte> pingBody, long serverTimeMs)
    {
        if (pingBody.Length < PingBodyBytes)
            return null;

        var pong = new byte[BodyBytes];
        pingBody[..ClientTimeBytes].CopyTo(pong);
        BinaryPrimitives.WriteInt64LittleEndian(pong.AsSpan(ClientTimeBytes), serverTimeMs);
        return pong;
    }

 /// <summary>
 /// Gets the server clock carried in the pong, in milliseconds. Derived from the high-resolution monotonic timer; the client only uses differences between values.
 /// </summary>
 /// <remarks>
 /// Whole seconds and the remainder are scaled separately, so the conversion cannot overflow on long uptimes.
 /// </remarks>
    public static long ServerTimeMs()
    {
        long ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency;
        return ticks / frequency * MillisecondsPerSecond + ticks % frequency * MillisecondsPerSecond / frequency;
    }
}
