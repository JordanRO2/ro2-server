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
/// RMI 1488 ReqGuildfundsInHandWithdraw — take zeny out of the guild vault's funds into the wallet.
/// </summary>
/// <remarks>
/// <para>
/// Request <c>[16 guild GUID][u64 amount]</c> from the vault window's withdraw button.
/// The client checks nothing before sending. The request is always 24 bytes.
/// Layouts and codes: <see cref="GuildFundsInHandPackets"/>.
/// </para>
/// <para>
/// Answer: every 1488 gets one 2673. A success is <c>[0][wallet after]</c>, followed at once by 2677
/// <c>[funds after]</c>; a refusal is <c>[39][0]</c> with no 2677, exactly when wallet + amount passes
/// 10,000,000,000 (Config_Def 91). 39 is checked before the funds (a 99,999,999,000 request gets 39, not 34).
/// No 2435 follows: the 2673 consumer sets the wallet from the u64 itself.
/// </para>
/// <para>
/// Order of the move: the funds leave the guild and are written FIRST, under the guild's gate (which also
/// re-checks that the guild holds the amount), and only then is the wallet credited and written; if the
/// wallet write fails, the funds are given back. So a failure can lose the move but never duplicate the
/// zeny. No rank permission is checked, and the item vault handlers (1485 / 1486) check none either.
/// </para>
/// </remarks>
public class GuildFundsWithdrawHandler : ZoneRmiHandlerBase<GuildFundsWithdrawHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildfundsInHandWithdraw;
    public override string Name => "GuildFundsWithdraw";
    public GuildFundsWithdrawHandler(IServiceScopeFactory s, ILogger<GuildFundsWithdrawHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (!GuildFundsInHandPackets.TryParseRequest(payload.Span, guild, out var amount, out var guidMatches))
        {
            Logger.LogWarning("{Name} sent a {Len}-byte ReqGuildfundsInHandWithdraw, need {Need}",
                player.Name, payload.Length, GuildFundsInHandPackets.RequestLength);
            return;
        }

        var cap = GuildFundsInHandPackets.WalletCap(services.GetRequiredService<GameDataStore>().ConfigDef);
        ushort result;
        long fundsAfter = 0;
        if (guild == null || !guidMatches) result = GuildFundsInHandPackets.ResultGuildNotFound;
        else if ((result = GuildFundsInHandPackets.WithdrawWalletRefusal(player.Gold, amount, cap)) == 0)
            
            (result, fundsAfter) = await MoveAsync(guild, player, (long)amount, services);

        Logger.LogInformation("Session {Id}: {Name} withdraws {Amount} zeny from guild {Guild} -> result {Result}, wallet {Wallet}, funds {Funds}",
            session.SessionId, player.Name, amount, guild?.Id ?? 0, result, player.Gold, fundsAfter);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildFundsInHandWithdraw,
            (ReadOnlyMemory<byte>)GuildFundsInHandPackets.BuildAnsWithdraw2673(result, player.Gold));
        if (result != 0 || guild == null) return;
        await session.SendRmiAsync(ZoneS2COpcodes.NfyUpdateGuildFundsInHand,
            (ReadOnlyMemory<byte>)GuildFundsInHandPackets.BuildNfyFundsInHand2677(fundsAfter));

        
        
        
        
        await services.GetRequiredService<IGuildStorageLog>()
            .AddFundsAsync(GuildStorageLog.Guid16Of(guild), player.Name, GuildStorageLog.KindWithdraw, (long)amount);
    }

 /// <summary>Take from the funds (written first), then credit the wallet; a failed wallet write gives the
 /// funds back. Returns the 2673 code and the funds after a successful move.</summary>
    private async Task<(ushort Result, long FundsAfter)> MoveAsync(Guild guild, PlayerState player, long amount, IServiceProvider services)
    {
        var (refusal, fundsAfter) = await GuildService.Instance.TakeFundsAsync(guild, amount);
        switch (refusal)
        {
            case GuildService.FundsRefusal.None: break;
            case GuildService.FundsRefusal.NotEnoughFunds: return (GuildFundsInHandPackets.ResultGuildFundsShort, 0);
            case GuildService.FundsRefusal.NotRegistered: return (GuildFundsInHandPackets.ResultGuildNotFound, 0);
            default: return (GuildFundsInHandPackets.ResultSystemError, 0);
        }

        player.Credit(amount);
        try
        {
            await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);
        }
        catch (Exception ex)
        {
            
            
            if (!player.TryDebit(amount))
            {
                Logger.LogError(ex, "{Name}: wallet write failed after a guild withdraw of {Amount}, but the zeny was already spent; kept as withdrawn", player.Name, amount);
                return (GuildFundsInHandPackets.ResultOk, fundsAfter);
            }
            await GuildService.Instance.ReturnFundsAsync(guild, amount);
            Logger.LogError(ex, "{Name}: wallet write failed after a guild withdraw of {Amount}; the funds were given back", player.Name, amount);
            return (GuildFundsInHandPackets.ResultSystemError, 0);
        }
        return (GuildFundsInHandPackets.ResultOk, fundsAfter);
    }
}
