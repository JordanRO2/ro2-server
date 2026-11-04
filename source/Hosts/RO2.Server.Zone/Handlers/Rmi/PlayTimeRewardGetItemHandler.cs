using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Rewards.Domain;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1643 ReqPlayTimeRewardGetItem: claims the current play-time reward slot.</summary>
/// <remarks>
/// <para>
/// Play-time progress is kept per account and shared by its characters. The answer (2945) carries the result,
/// the next slot and its remaining seconds.
/// </para>
/// <para>
/// A granted reward is filed into the account gift box (<see cref="AccountGiftStore"/>) and is collected from
/// there. The gift box has no cell limit, so a ready slot is always granted.
/// </para>
/// </remarks>
public class PlayTimeRewardGetItemHandler : ZoneRmiHandlerBase<PlayTimeRewardGetItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPlayTimeRewardGetItem;
    public override string Name => "PlayTimeRewardGetItem";
    public PlayTimeRewardGetItemHandler(IServiceScopeFactory s, ILogger<PlayTimeRewardGetItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        uint requestedSlot = payload.Length >= 4
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Span)
            : 0u;

        var rewards = services.GetRequiredService<PlayTimeRewardStore>();
        rewards.EnsureClockRunning(services);
        var store = services.GetRequiredService<GameDataStore>();
        var chain = PlayTimeRewardChain.Chain(store);
        var progress = await rewards.GetAsync(player, store, Logger, ct);
        if (progress is null)
            return; 

        
        
        var gifts = services.GetRequiredService<IAccountGiftRepository>();
        try
        {
            await gifts.GetLiveAsync(player.AccountId, ServerWallClock.Now().DateTime, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "{Name}: 1643 for slot {Slot} not answered, the gift box could not be read", player.Name, requestedSlot);
            return;
        }

        var outcome = await services.GetRequiredService<RO2.Rewards.Application.ClaimPlayTimeRewardUseCase>().ExecuteAsync(
            progress, chain, requestedSlot, player.LoginTime, DateTime.UtcNow, player.AccountId,
            () => Convert.ToHexString(Guid.NewGuid().ToByteArray()), ServerWallClock.Now().DateTime, ct);

        if (outcome.Claim is not { } claim)
        {
            Logger.LogWarning("{Name}: 1643 for slot {Slot} not answered, Attendance.json has no Type-1 (play-time) row",
                player.Name, requestedSlot);
            return;
        }

        Logger.LogDebug("{Name} claims play-time slot {Slot}: result {Result}, answer slot {Next} / {Seconds}s",
            player.Name, requestedSlot, claim.Result, claim.Slot, claim.Seconds);

        if (outcome.FilingError is { } error)
            Logger.LogError(error, "{Name}: play-time slot {Slot} reward could not be filed in the gift box, claim not advanced", player.Name, claim.Slot);
        else if (player.AccountId == 0)
            Logger.LogWarning("{Name}: play-time claim with no account id; nothing filed, claim not advanced", player.Name);

        if (claim.GrantedRow is { } row)
        {
            if (outcome.Filed.Count > 0)
                Logger.LogInformation("{Name}: play-time slot {Slot} reward filed in the gift box ({Items}), {Total} gift(s) in it",
                    player.Name, row.ID, string.Join(", ", outcome.Filed.Select(g => $"{g.Count}x {g.ItemId}")), outcome.GiftsInBox);
            await rewards.SaveAsync(player.AccountId, progress, chain, Logger, ct);
        }

        await session.SendRmiAsync(ZoneS2COpcodes.AnsPlayTimeRewardGetItem,
            PlayTimeRewardPackets.BuildAnswer(claim.Result, claim.Slot, claim.Seconds), ct);

        if (outcome.GiftsInBox > 0)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyAccountGiftReceived, AccountGiftPackets.Build2798(outcome.GiftsInBox), ct);
    }
}
