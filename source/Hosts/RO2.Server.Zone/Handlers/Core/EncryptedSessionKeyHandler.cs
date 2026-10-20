using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Zone.Handlers.Core;

public class EncryptedSessionKeyHandler : EncryptedSessionKeyHandlerBase<EncryptedSessionKeyHandler>, IZoneCoreHandler
{
    public EncryptedSessionKeyHandler(ILogger<EncryptedSessionKeyHandler> logger) : base(logger) { }
}
