using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Login.Handlers.Core;

public class ServerConnectionRequestDataHandler : ServerConnectionRequestDataHandlerBase<ServerConnectionRequestDataHandler>, ILoginCoreHandler
{
    public ServerConnectionRequestDataHandler(ILogger<ServerConnectionRequestDataHandler> logger) : base(logger) { }
}
