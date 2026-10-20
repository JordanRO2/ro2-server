using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1492 ReqGuildStorageStatement_Zeny: returns one page of the guild vault funds history.
/// </summary>
/// <remarks>
/// <para>
/// The request body is <c>[16-byte guild GUID][u8 page][u8 days]</c>. Pages are zero-based; days is the history window (7 or 30 in the client's "Last Week" and "Last Month" views).
/// </para>
/// <para>
/// The reply, AnsGuildStorageStatement_Zeny (2679), carries the requested page of the caller's guild funds lines within the day window, newest first. Paging follows <see cref="IGuildStorageLog.GetFundsPageAsync"/> and the layout follows <see cref="GuildStorageLog.BuildAnsStatementZeny2679"/>. A caller without a guild, or a guild with no lines in the window, gets a header-only answer.
/// </para>
/// <para>
/// The lines are this server's funds moves, logged by <see cref="GuildFundsDepositHandler"/> and <see cref="GuildFundsWithdrawHandler"/>. As with the item history (1491), the caller's own guild is paged; the GUID in the request is not used to find it.
/// </para>
/// </remarks>
public class GuildStorageStatementZenyHandler : ZoneRmiHandlerBase<GuildStorageStatementZenyHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildStorageStatement_Zeny;
    public override string Name => "GuildStorageStatementZeny";

 /// <summary>
 /// Expected length of the 1492 request body, in bytes.
 /// </summary>
    private const int RequestLength = 18;

    public GuildStorageStatementZenyHandler(IServiceScopeFactory s, ILogger<GuildStorageStatementZenyHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength)
        {
            Logger.LogWarning("{Name} sent a {Len}-byte ReqGuildStorageStatement_Zeny, need {Need}",
                player.Name, payload.Length, RequestLength);
            return;
        }
        byte page = payload.Span[16];
        byte days = payload.Span[17];

        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        var config = services.GetRequiredService<GameDataStore>().ConfigDef;
        int pageSize = GuildStorageLog.HistoryPageSize(config);
        
        
        int window = days > 0 ? days : GuildStorageLog.HistoryRetentionDays(config);

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        long total = 0;
        IReadOnlyList<GuildFundsLogEntry> lines = Array.Empty<GuildFundsLogEntry>();
        if (guild != null)
            (total, lines) = await services.GetRequiredService<IGuildStorageLog>()
                .GetFundsPageAsync(GuildStorageLog.Guid16Of(guild), page, window, pageSize);

        Logger.LogDebug("{Name} reads guild storage funds history page {Page} ({Days} days): {Count}/{Total} lines",
            player.Name, page, window, lines.Count, total);

        var body = GuildStorageLog.BuildAnsStatementZeny2679(total, lines);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildStorageStatement_Zeny, (ReadOnlyMemory<byte>)body);
    }
}
