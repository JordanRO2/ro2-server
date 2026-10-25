using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using Base1642 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqPlayTimeRewardHandler>;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1642 ReqPlayTimeReward (empty body). Answered with 2944 AnsPlayTimeReward, the play-time reward poll.</summary>
/// <remarks>
/// The answer is 10 bytes: <c>[u16 result][u32 slot][u32 secondsLeft]</c>. The slot is the play-time row the
/// account is working toward, and secondsLeft is the remaining online time for that row. Progress comes from
/// <see cref="PlayTimeRewardStore"/> using the rules in <see cref="PlayTimeRewardChain"/>, keyed by account, so
/// the poll and the claim (1643) always agree and progress persists across relogs and daily resets.
/// No answer is sent when there is no player state or when stored progress cannot be read.
/// </remarks>
public class ReqPlayTimeRewardHandler : Base1642
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPlayTimeReward;
    public override string Name => "ReqPlayTimeReward";

    public ReqPlayTimeRewardHandler(IServiceScopeFactory s, ILogger<ReqPlayTimeRewardHandler> l) : base(s, l)
    {
        
        
        
        try
        {
            using var scope = s.CreateScope();
            scope.ServiceProvider.GetRequiredService<PlayTimeRewardStore>().EnsureClockRunning(scope.ServiceProvider);
        }
        catch (Exception ex)
        {
            l.LogWarning(ex, "Play-time reward clock could not be started at wiring; the next 1642/1643 retries");
        }
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        var player = services.GetRequiredService<PlayerStateManager>().GetPlayer(session.SessionId);
        if (player is null)
        {
            Logger.LogDebug("Session {Id}: {Name} before the PlayerState exists; not answered", session.SessionId, Name);
            return;
        }

        var rewards = services.GetRequiredService<PlayTimeRewardStore>();
        rewards.EnsureClockRunning(services);
        var store = services.GetRequiredService<GameDataStore>();
        var chain = PlayTimeRewardChain.Chain(store);
        var progress = await rewards.GetAsync(player, store, Logger, ct);
        if (progress is null)
            return;

        (uint Slot, uint RemainingSeconds)? answer;
        lock (progress.Gate)
            answer = PlayTimeRewardChain.Peek(progress, chain, player.LoginTime, DateTime.UtcNow);

        if (answer is not { } a)
        {
            Logger.LogWarning("{Name}: {Handler} not answered, Attendance.json has no Type-1 (play-time) row", player.Name, Name);
            return;
        }

        Logger.LogDebug("{Name}: {Handler} -> slot {Slot}, {Seconds}s left", player.Name, Name, a.Slot, a.RemainingSeconds);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPlayTimeReward,
            PlayTimeRewardChain.BuildAnswer(PlayTimeRewardChain.ResultOk, a.Slot, a.RemainingSeconds), ct);
    }
}
