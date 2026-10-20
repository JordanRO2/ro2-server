using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Zone.Handlers.Core;

public class UnreliablePingHandler : UnreliablePingHandlerBase<UnreliablePingHandler>, IZoneCoreHandler
{
    public UnreliablePingHandler(ILogger<UnreliablePingHandler> logger) : base(logger) { }
}
