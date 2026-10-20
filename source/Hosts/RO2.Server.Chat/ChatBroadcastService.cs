using System.Linq;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat;

/// <summary>
/// Holds the Chat <see cref="ProudServer"/> so that handlers can broadcast to every connected session.
/// All players join the community channels automatically, so the channel members are all sessions. The channel
/// GUID or index on each message tells the client which tab to show it in.
/// </summary>
public class ChatBroadcastService
{
    private ProudServer? _server;

    public void SetServer(ProudServer server) => _server = server;

    public IReadOnlyCollection<ProudSession> Sessions =>
        _server is null ? Array.Empty<ProudSession>() : _server.GetSessions().ToArray();
}
