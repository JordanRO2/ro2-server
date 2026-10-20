using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Login.Handlers.Core;

public class ReliablePingHandler : ReliablePingHandlerBase<ReliablePingHandler>, ILoginCoreHandler
{
    public ReliablePingHandler(ILogger<ReliablePingHandler> logger) : base(logger) { }
}
