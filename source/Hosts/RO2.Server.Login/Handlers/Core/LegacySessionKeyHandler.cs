using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Login.Handlers.Core;

public class LegacySessionKeyHandler : LegacySessionKeyHandlerBase<LegacySessionKeyHandler>, ILoginCoreHandler
{
    public LegacySessionKeyHandler(ILogger<LegacySessionKeyHandler> logger) : base(logger) { }
}
