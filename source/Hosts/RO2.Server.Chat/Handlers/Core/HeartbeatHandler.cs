using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Chat.Handlers.Core;

public class HeartbeatHandler : HeartbeatHandlerBase<HeartbeatHandler>, IChatMultiCoreHandler
{
    public HeartbeatHandler(ILogger<HeartbeatHandler> logger) : base(logger) { }
}
