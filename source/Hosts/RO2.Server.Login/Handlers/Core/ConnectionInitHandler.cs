using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Login.Handlers.Core;

public class ConnectionInitHandler : ConnectionInitHandlerBase<ConnectionInitHandler>, ILoginCoreHandler
{
    public ConnectionInitHandler(ILogger<ConnectionInitHandler> logger) : base(logger) { }
}
