using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Login.Handlers.Core;

public class EncryptedSessionKeyHandler : EncryptedSessionKeyHandlerBase<EncryptedSessionKeyHandler>, ILoginCoreHandler
{
    public EncryptedSessionKeyHandler(ILogger<EncryptedSessionKeyHandler> logger) : base(logger) { }
}
