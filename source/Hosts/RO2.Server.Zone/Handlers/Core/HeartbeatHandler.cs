using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Zone.Handlers.Core;

public class HeartbeatHandler : HeartbeatHandlerBase<HeartbeatHandler>, IZoneMultiCoreHandler
{
    public HeartbeatHandler(ILogger<HeartbeatHandler> logger) : base(logger) { }
}
