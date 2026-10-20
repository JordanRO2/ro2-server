using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;

namespace RO2.Network.Handlers.Shared;

/// <summary>
/// Handles the speed-hack detector probe (<see cref="ProudCoreOpCode.SpeedHackDetectorPing"/>, 0x1C). The probe carries no body and needs no reply, so this handler sends nothing.
/// </summary>
public abstract class HeartbeatHandlerBase<THandler> : MultiCoreHandlerBase<THandler>
    where THandler : class
{
    public override IEnumerable<ProudCoreOpCode> Opcodes => [ProudCoreOpCode.SpeedHackDetectorPing];

    public override string Name => "SpeedHackDetectorPing";

    protected HeartbeatHandlerBase(ILogger<THandler> logger) : base(logger) { }

    protected override Task HandleCoreAsync(
        ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => Task.CompletedTask;
}
