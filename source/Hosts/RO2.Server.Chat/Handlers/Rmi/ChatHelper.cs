using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

internal static class ChatHelper
{
    /// <summary>
    /// Body of an empty messenger list reply, 8 bytes: <c>[u16 reserved = 0][u32 innerLen = 2][u16 count = 0]</c>.
    /// The inner length covers just the u16 count. Built fresh per call so no caller can mutate a shared array.
    /// </summary>
    private static byte[] BuildEmptyListBody() => new byte[]
    {
        0x00, 0x00,              // reserved u16 = 0
        0x02, 0x00, 0x00, 0x00,  // innerLen u32 = 2 (the count field that follows)
        0x00, 0x00,              // count u16 = 0 (empty list)
    };

    /// <summary>
    /// Answers a messenger list request with an empty list. Sent unencrypted (internal RMI path): the client
    /// ignores the encrypted path for chat-server replies, so an encrypted list reply is never seen and the
    /// client keeps retrying the request, which hangs the messenger UI.
    /// </summary>
    internal static async Task SendEmptyList(ProudSession session, ushort rmiId)
    {
        await session.SendInternalRmiAsync(rmiId, BuildEmptyListBody());
    }
}
