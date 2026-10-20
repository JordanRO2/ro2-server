using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Zone.Handlers.Core;

public class LegacySessionKeyHandler : LegacySessionKeyHandlerBase<LegacySessionKeyHandler>, IZoneCoreHandler
{
    public LegacySessionKeyHandler(ILogger<LegacySessionKeyHandler> logger) : base(logger) { }
}
