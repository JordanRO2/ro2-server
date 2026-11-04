using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Worlds.Domain;

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

    /// <summary>
    /// Sends to the players inside <paramref name="viewer"/>'s area-of-interest box (its <see cref="PlayerState.VisiblePcs"/>).
    /// Area-of-interest membership is symmetric, so these are exactly the players who can see the viewer; used to scope a
    /// mover's 2750 relay to the peers that actually have its avatar.
    /// </summary>
    public async Task BroadcastToVisiblePcsAsync(PlayerState viewer, ushort opcode, Action<PacketWriter> writeAction)
    {
        if (_server == null) return;

        uint[] handles;
        lock (viewer.AoiGate) handles = viewer.VisiblePcs.ToArray();
        if (handles.Length == 0) return;

        var payload = BuildPayload(writeAction);
        var visible = handles.ToHashSet();
        foreach (var player in _playerManager.GetPlayersOnMap(viewer.MapId))
        {
            if (!visible.Contains(player.EntityId)) continue;
            var session = _server.GetSession(player.SessionId);
            if (session != null)
                await session.SendInternalRmiAsync(opcode, payload);
        }
    }

    /// <summary>
    /// Sends to the players on <paramref name="mapId"/> whose area-of-interest holds the NPC handle
    /// <paramref name="npcHandle"/> (their <see cref="PlayerState.VisibleNpcs"/>). Scopes NPC-sourced events (a move
    /// 2318, a respawn 2317) to the viewers that were actually announced the NPC, so a culled NPC is never referenced
    /// by a handle the client does not hold. Reads each viewer's set under its AoiGate because the caller is the NPC
    /// tick thread, concurrent with the viewer's own session recompute.
    /// </summary>
    public async Task BroadcastToNpcViewersAsync(uint mapId, uint npcHandle, ushort opcode, Action<PacketWriter> writeAction)
    {
        if (_server == null) return;

        var payload = BuildPayload(writeAction);
        foreach (var player in _playerManager.GetPlayersOnMap(mapId))
        {
            bool sees;
            lock (player.AoiGate) sees = player.VisibleNpcs.Contains(npcHandle);
            if (!sees) continue;
            var session = _server.GetSession(player.SessionId);
            if (session != null)
                await session.SendInternalRmiAsync(opcode, payload);
        }
    }

    /// <summary>
    /// Announces an NPC (a respawn 2317) to the players on <paramref name="mapId"/> whose area-of-interest box contains
    /// (<paramref name="actorX"/>, <paramref name="actorY"/>), and seeds the handle into each such viewer's
    /// <see cref="PlayerState.VisibleNpcs"/> so a later move recompute does not re-announce it. Scoped by position, not by
    /// membership, because a respawn carries a fresh handle no viewer holds yet.
    /// </summary>
    public async Task AnnounceNpcToBoxAsync(uint mapId, uint npcHandle, float actorX, float actorY, float half,
        ushort opcode, Action<PacketWriter> writeAction)
    {
        if (_server == null) return;

        var payload = BuildPayload(writeAction);
        foreach (var player in _playerManager.GetPlayersOnMap(mapId))
        {
            if (!AoiRules.InView(player.X, player.Y, actorX, actorY, half)) continue;
            lock (player.AoiGate) player.VisibleNpcs.Add(npcHandle);
            var session = _server.GetSession(player.SessionId);
            if (session != null)
                await session.SendInternalRmiAsync(opcode, payload);
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
