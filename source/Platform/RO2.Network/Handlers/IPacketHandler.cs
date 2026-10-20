using RO2.Network.ProudNet;

namespace RO2.Network.Handlers;

/// <summary>
/// Base interface for all packet handlers
/// </summary>
public interface IPacketHandler
{
 /// <summary>
 /// Handler name for logging and debugging
 /// </summary>
    string Name { get; }

 /// <summary>
 /// Handle an incoming packet
 /// </summary>
 /// <param name="session">The session that received the packet</param>
 /// <param name="payload">The packet payload</param>
 /// <param name="ct">Cancellation token</param>
    Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct = default);
}

/// <summary>
/// Interface for RMI (Remote Method Invocation) packet handlers.
/// Used for game logic packets like login, movement, skills, etc.
/// </summary>
public interface IRmiHandler : IPacketHandler
{
 /// <summary>
 /// The RMI opcode this handler processes (e.g., <see cref="RO2.Network.Protocol.LoginC2SOpcodes.ReqVersionCheck"/>, <see cref="RO2.Network.Protocol.LoginC2SOpcodes.ReqLogin"/>)
 /// </summary>
    ushort Opcode { get; }
}

/// <summary>
/// Interface for ProudNet core protocol handlers.
/// Used for internal handshake and connection management packets.
/// </summary>
public interface ICoreHandler : IPacketHandler
{
 /// <summary>
 /// The core opcode this handler processes (e.g., <see cref="ProudCoreOpCode.RequestServerConnectionHint"/>, <see cref="ProudCoreOpCode.EncryptedSessionKey"/>)
 /// </summary>
    ProudCoreOpCode Opcode { get; }
}

/// <summary>
/// Interface for handlers that need to process multiple core opcodes.
/// Used for heartbeat handlers that handle both <see cref="ProudCoreOpCode.UnreliablePing"/> and <see cref="ProudCoreOpCode.SpeedHackDetectorPing"/>.
/// </summary>
public interface IMultiCoreHandler : IPacketHandler
{
 /// <summary>
 /// All core opcodes this handler processes
 /// </summary>
    IEnumerable<ProudCoreOpCode> Opcodes { get; }
}

/// <summary>
/// Marker interface for Login server RMI handlers
/// </summary>
public interface ILoginRmiHandler : IRmiHandler { }

/// <summary>
/// Marker interface for Login server core handlers
/// </summary>
public interface ILoginCoreHandler : ICoreHandler { }

/// <summary>
/// Marker interface for Login server multi-core handlers
/// </summary>
public interface ILoginMultiCoreHandler : IMultiCoreHandler { }

/// <summary>
/// Marker interface for Zone server RMI handlers
/// </summary>
public interface IZoneRmiHandler : IRmiHandler { }

/// <summary>
/// Marker interface for Zone server core handlers
/// </summary>
public interface IZoneCoreHandler : ICoreHandler { }

/// <summary>
/// Marker interface for Zone server multi-core handlers
/// </summary>
public interface IZoneMultiCoreHandler : IMultiCoreHandler { }

/// <summary>
/// Marker interface for Chat server RMI handlers
/// </summary>
public interface IChatRmiHandler : IRmiHandler { }

/// <summary>
/// Marker interface for Chat server core handlers
/// </summary>
public interface IChatCoreHandler : ICoreHandler { }

/// <summary>
/// Marker interface for Chat server multi-core handlers
/// </summary>
public interface IChatMultiCoreHandler : IMultiCoreHandler { }
