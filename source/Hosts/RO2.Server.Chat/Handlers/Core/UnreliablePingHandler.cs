using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Chat.Handlers.Core;

public class UnreliablePingHandler : UnreliablePingHandlerBase<UnreliablePingHandler>, IChatCoreHandler
{
    public UnreliablePingHandler(ILogger<UnreliablePingHandler> logger) : base(logger) { }
}
