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
/// Handles RMI 1491 ReqGuildStorageStatement_Item: returns one page of the guild vault item history.
/// </summary>
/// <remarks>
/// <para>
/// The request body is <c>[16-byte guild GUID][u8 page][u8 period]</c>. The page is zero-based and the period selects the day window (7 days).
/// </para>
/// <para>
/// The reply, AnsGuildStorageStatement_Item (2678), carries the requested page of the caller's guild item log lines. See <see cref="GuildStorageLog"/> for the line layout.
/// </para>
/// </remarks>
public class GuildStorageStatementItemHandler : ZoneRmiHandlerBase<GuildStorageStatementItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildStorageStatement_Item;
    public override string Name => "GuildStorageStatementItem";
    public GuildStorageStatementItemHandler(IServiceScopeFactory s, ILogger<GuildStorageStatementItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 18) return;
        byte page = payload.Span[16];
        byte period = payload.Span[17];

        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());

        
        
        
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        long total = 0;
        IReadOnlyList<GuildStorageLogEntry> lines = Array.Empty<GuildStorageLogEntry>();
        if (guild != null)
            (total, lines) = await services.GetRequiredService<IGuildStorageLog>()
                .GetPageAsync(GuildStorageLog.Guid16Of(guild), page, period, GuildStorageLog.PageSize);

        Logger.LogDebug("{Name} reads guild storage item history page {Page} ({Days} days): {Count}/{Total} lines",
            player.Name, page, period, lines.Count, total);

        var body = GuildStorageLog.BuildAnsStatementItem2678(total, lines);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildStorageStatement_Item, (ReadOnlyMemory<byte>)body);
    }
}
