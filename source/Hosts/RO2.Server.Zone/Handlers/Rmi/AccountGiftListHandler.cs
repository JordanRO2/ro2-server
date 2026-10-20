using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1550 ReqAccountGiftList, which requests one page of the account gift box.
/// </summary>
/// <remarks>
/// The reply (2799 NfyAccountGiftInfo) carries the account id, the character GID, the requested page, the total
/// gift count and up to seven 43-byte gift records, newest first. An empty box is answered with an empty page.
/// A box that cannot be read is not answered.
/// Requests arriving within <see cref="AnswerThrottleMs"/> of the previous answered request are not answered.
/// </remarks>
public class AccountGiftListHandler : ZoneRmiHandlerBase<AccountGiftListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAccountGiftList;
    public override string Name => "AccountGiftList";
 /// <summary>
 /// Minimum interval in milliseconds between two answered 1550 requests of a session. A request sooner than
 /// this after the last answered one is not answered.
 /// </summary>
    internal const long AnswerThrottleMs = 500;

 /// <summary>
 /// Time of the player's last answered 1550 request, in <c>Environment.TickCount64</c> milliseconds. Held
 /// weakly, so it is released together with the zone session.
 /// </summary>
    private static readonly ConditionalWeakTable<PlayerState, StrongBox<long>> LastAnswered = new();

 /// <summary>
 /// Whether a request at <paramref name="nowMs"/> is throttled, given the time of the last answered request
 /// <paramref name="lastAnsweredMs"/> (null when none has been answered yet).
 /// </summary>
    internal static bool IsThrottled(long? lastAnsweredMs, long nowMs)
        => lastAnsweredMs is long last && nowMs - last < AnswerThrottleMs;

    public AccountGiftListHandler(IServiceScopeFactory s, ILogger<AccountGiftListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 2)
        {
            Logger.LogDebug("{Name}: 1550 without its u16 page ({Len} B), not answered", player.Name, payload.Length);
            return;
        }
        var page = new PacketReader(payload.Span).ReadUInt16();

        long now = Environment.TickCount64;
        if (IsThrottled(LastAnswered.TryGetValue(player, out var stamp) ? stamp.Value : null, now))
        {
            Logger.LogDebug("{Name}: 1550 page {Page} {Ms} ms after the last answered one; not answered (retail drops it)",
                player.Name, page, now - stamp!.Value);
            return;
        }

        var nowLocal = ServerWallClock.Now().DateTime;
        IReadOnlyList<AccountGift> gifts;
        try
        {
            gifts = player.AccountId == 0
                ? new List<AccountGift>()
                : await services.GetRequiredService<IAccountGiftRepository>().GetLiveAsync(player.AccountId, nowLocal, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "{Name}: 1550 page {Page} not answered, the gift box could not be read", player.Name, page);
            return;
        }

        Logger.LogDebug("{Name} requests account gift page {Page}: {Total} gift(s) in the box", player.Name, page, gifts.Count);

        
        await session.SendRmiAsync(ZoneS2COpcodes.NfyAccountGiftInfo,
            AccountGiftPackets.Build2799(player.AccountId, player.CharacterUid, page, gifts, nowLocal), ct);
        
        LastAnswered.AddOrUpdate(player, new StrongBox<long>(now));
    }
}
