using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Chat.Handlers.Core;

public class ReliablePingHandler : ReliablePingHandlerBase<ReliablePingHandler>, IChatCoreHandler
{
    public ReliablePingHandler(ILogger<ReliablePingHandler> logger) : base(logger) { }
}
