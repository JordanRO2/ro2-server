using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Login.Handlers.Core;

public class HeartbeatHandler : HeartbeatHandlerBase<HeartbeatHandler>, ILoginMultiCoreHandler
{
    public HeartbeatHandler(ILogger<HeartbeatHandler> logger) : base(logger) { }
}
