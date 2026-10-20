using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Chat.Handlers.Core;

public class ServerConnectionRequestDataHandler : ServerConnectionRequestDataHandlerBase<ServerConnectionRequestDataHandler>, IChatCoreHandler
{
    public ServerConnectionRequestDataHandler(ILogger<ServerConnectionRequestDataHandler> logger) : base(logger) { }
}
