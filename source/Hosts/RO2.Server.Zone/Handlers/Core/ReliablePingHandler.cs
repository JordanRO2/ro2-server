using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Zone.Handlers.Core;

public class ReliablePingHandler : ReliablePingHandlerBase<ReliablePingHandler>, IZoneCoreHandler
{
    public ReliablePingHandler(ILogger<ReliablePingHandler> logger) : base(logger) { }
}
