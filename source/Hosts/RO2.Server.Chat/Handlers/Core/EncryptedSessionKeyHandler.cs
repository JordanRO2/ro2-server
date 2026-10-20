using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;

namespace RO2.Server.Chat.Handlers.Core;

public class EncryptedSessionKeyHandler : EncryptedSessionKeyHandlerBase<EncryptedSessionKeyHandler>, IChatCoreHandler
{
    public EncryptedSessionKeyHandler(ILogger<EncryptedSessionKeyHandler> logger) : base(logger) { }
}
