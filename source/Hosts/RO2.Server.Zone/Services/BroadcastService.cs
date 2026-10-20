using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Sends packets to players on a map, to nearby players, or to every online player.
/// </summary>
/// <remarks>
/// Game server-to-client RMIs are sent over the internal (unencrypted, opcode 0x01) transport.
/// </remarks>
public class BroadcastService
{
    private readonly PlayerStateManager _playerManager;
    private readonly ILogger<BroadcastService> _logger;
    private ProudServer? _server;

    public BroadcastService(PlayerStateManager playerManager, ILogger<BroadcastService> logger)
    {
        _playerManager = playerManager;
        _logger = logger;
    }

    public void SetServer(ProudServer server)
    {
        _server = server;
    }

    public ProudSession? GetSession(uint sessionId)
    {
        return _server?.GetSession(sessionId);
    }

    private static byte[] BuildPayload(Action<PacketWriter> writeAction)
    {
        using var pw = new PacketWriter();
        writeAction(pw);
        return pw.GetWrittenSpan().ToArray();
    }

    public async Task BroadcastToNearbyAsync(PlayerState source, ushort opcode, Action<PacketWriter> writeAction)
    {
        if (_server == null)
        {
            _logger.LogWarning("Cannot broadcast: Server not initialized");
            return;
        }

        var payload = BuildPayload(writeAction);
        var nearbyPlayers = _playerManager.GetPlayersOnMap(source.MapId)
            .Where(p => p.SessionId != source.SessionId)
            .ToList();

        foreach (var player in nearbyPlayers)
        {
            var session = _server.GetSession(player.SessionId);
            if (session != null)
            {
                await session.SendInternalRmiAsync(opcode, payload);
            }
        }
    }

    public async Task BroadcastToMapAsync(uint mapId, ushort opcode, Action<PacketWriter> writeAction, uint? excludeSessionId = null)
    {
        if (_server == null) return;

        var payload = BuildPayload(writeAction);
        var players = _playerManager.GetPlayersOnMap(mapId)
            .Where(p => excludeSessionId == null || p.SessionId != excludeSessionId)
            .ToList();

        foreach (var player in players)
        {
            var session = _server.GetSession(player.SessionId);
            if (session != null)
            {
                await session.SendInternalRmiAsync(opcode, payload);
            }
        }
    }

    public async Task BroadcastToAllAsync(ushort opcode, Action<PacketWriter> writeAction)
    {
        if (_server == null) return;

        var payload = BuildPayload(writeAction);
        foreach (var player in _playerManager.GetAllPlayers())
        {
            var session = _server.GetSession(player.SessionId);
            if (session != null)
            {
                await session.SendInternalRmiAsync(opcode, payload);
            }
        }
    }
}
