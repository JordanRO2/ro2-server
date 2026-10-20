using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Zone.Handlers.Core;

public class ServerConnectionRequestDataHandler : ServerConnectionRequestDataHandlerBase<ServerConnectionRequestDataHandler>, IZoneCoreHandler
{
    public ServerConnectionRequestDataHandler(ILogger<ServerConnectionRequestDataHandler> logger) : base(logger) { }
}
