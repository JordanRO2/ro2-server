using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1487 ReqGuildFundsInHandDeposit — put zeny from the wallet into the guild vault's funds.
/// </summary>
/// <remarks>
/// <para>
/// The request and answer layouts follow the client's reader and writer logic and its twin 1488 / 2673
/// (<see cref="GuildFundsWithdrawHandler"/>). Request <c>[16 guild GUID][u64 amount]</c>, from the vault
/// window's deposit button, which checks wallet &gt;= amount itself (string_ui
/// 8805). See
/// <see cref="GuildFundsInHandPackets"/> for the layouts and the codes.
/// </para>
/// <para>
/// Order: guild and GUID (7), wallet (46), storage limit (33), then the move. The wallet is debited and
/// written first; the funds are then added under the guild's gate, which checks the limit again (a second
/// deposit may have filled the vault meanwhile), and written; if that fails or refuses, the wallet gets its
/// zeny back. Success answers 2672 <c>[0][wallet after]</c> and then 2677 <c>[funds after]</c>, the order
/// and pairing used for every successful 1488. The 2672 consumer
/// sets the wallet from its u64, so no 2435 is sent. A refusal answers 2672 <c>[code][0]</c> and no 2677.
/// </para>
/// <para>
/// No rank permission is checked, and the item vault
/// handlers (1485 / 1486) check none either.
/// </para>
/// </remarks>
public class GuildFundsDepositHandler : ZoneRmiHandlerBase<GuildFundsDepositHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildFundsInHandDeposit;
    public override string Name => "GuildFundsDeposit";
    public GuildFundsDepositHandler(IServiceScopeFactory s, ILogger<GuildFundsDepositHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (!GuildFundsInHandPackets.TryParseRequest(payload.Span, guild, out var amount, out var guidMatches))
        {
            Logger.LogWarning("{Name} sent a {Len}-byte ReqGuildFundsInHandDeposit, need {Need}",
                player.Name, payload.Length, GuildFundsInHandPackets.RequestLength);
            return;
        }

        var data = services.GetRequiredService<GameDataStore>();
        ushort result;
        long fundsAfter = 0;
        if (guild == null || !guidMatches) result = GuildFundsInHandPackets.ResultGuildNotFound;
        else if ((result = GuildFundsInHandPackets.DepositWalletRefusal(player.Gold, amount)) == 0)
        {
            long limit;
            long funds;
            lock (guild.Gate)
            {
                limit = GuildFundsInHandPackets.StorageLimit(guild.Skills, GuildSkillLearnPackets.Index(data.GuildSkillLevel));
                funds = guild.Gold;
            }
            var move = (long)amount;   
            if (funds > limit || move > limit - funds) result = GuildFundsInHandPackets.ResultStorageLimit;
            else (result, fundsAfter) = await MoveAsync(guild, player, move, limit, services);
        }

        Logger.LogInformation("Session {Id}: {Name} deposits {Amount} zeny into guild {Guild} -> result {Result}, wallet {Wallet}, funds {Funds}",
            session.SessionId, player.Name, amount, guild?.Id ?? 0, result, player.Gold, fundsAfter);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildFundsInHandDeposit,
            (ReadOnlyMemory<byte>)GuildFundsInHandPackets.BuildAnsDeposit2672(result, player.Gold));
        if (result != 0 || guild == null) return;
        await session.SendRmiAsync(ZoneS2COpcodes.NfyUpdateGuildFundsInHand,
            (ReadOnlyMemory<byte>)GuildFundsInHandPackets.BuildNfyFundsInHand2677(fundsAfter));

        
        
        
        
        await services.GetRequiredService<IGuildStorageLog>()
            .AddFundsAsync(GuildStorageLog.Guid16Of(guild), player.Name, GuildStorageLog.KindDeposit, (long)amount);
    }

 /// <summary>Debit the wallet, then add to the funds; on a funds refusal or a failed guild write the wallet
 /// is credited back. Returns the 2672 code and the funds after a successful add.</summary>
    private async Task<(ushort Result, long FundsAfter)> MoveAsync(Guild guild, PlayerState player, long amount, long limit, IServiceProvider services)
    {
        var characters = services.GetRequiredService<ICharacterRepository>();
        
        if (!player.TryDebit(amount)) return (GuildFundsInHandPackets.ResultWalletShort, 0);
        SyncSnapshot(player);
        try
        {
            await characters.UpdateGoldAsync(player.CharacterId, player.Gold);
        }
        catch (Exception ex)
        {
            player.Credit(amount);
            SyncSnapshot(player);
            Logger.LogError(ex, "{Name}: wallet write failed before a guild deposit of {Amount}; nothing moved", player.Name, amount);
            return (GuildFundsInHandPackets.ResultSystemError, 0);
        }

        var (refusal, fundsAfter) = await GuildService.Instance.AddFundsAsync(guild, amount, limit);
        if (refusal == GuildService.FundsRefusal.None) return (GuildFundsInHandPackets.ResultOk, fundsAfter);

        
        player.Credit(amount);
        SyncSnapshot(player);
        try
        {
            await characters.UpdateGoldAsync(player.CharacterId, player.Gold);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Name}: wallet refund of {Amount} after a refused guild deposit did not persist (live wallet restored)",
                player.Name, amount);
        }
        return (refusal switch
        {
            GuildService.FundsRefusal.OverLimit => GuildFundsInHandPackets.ResultStorageLimit,
            GuildService.FundsRefusal.NotRegistered => GuildFundsInHandPackets.ResultGuildNotFound,
            _ => GuildFundsInHandPackets.ResultSystemError,
        }, 0);
    }

    private static void SyncSnapshot(PlayerState player)
    {
    }
}
