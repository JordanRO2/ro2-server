using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1468 ReqGuildModifyAuthority: the guild master saves one title in the guild's rank table.
/// </summary>
/// <remarks>
/// <para>
/// The request is 40 bytes: [16-byte guild GUID][u8 rank index][11-character rank name, zero-filled][u8 permissions]. The client sends one request per edited row. An accepted change is stored in the guild's rank table, answered with 2633, and broadcast to the guild as a 2637 rank record.
/// </para>
/// <para>
/// The 2633 answer is [u16 result]. Refusals: 7 (no guild), 17 (the requester is not the master), 20 (another index already uses the title) and 21 (index outside 0..9). Each request is applied in arrival order, so a row that collides only with a row renamed earlier in the same save is accepted.
/// </para>
/// </remarks>
public class GuildModifyAuthorityHandler : ZoneRmiHandlerBase<GuildModifyAuthorityHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildModifyAuthority;
    public override string Name => "GuildModifyAuthority";
    public GuildModifyAuthorityHandler(IServiceScopeFactory s, ILogger<GuildModifyAuthorityHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    private const int GuidLength = 16;
    private const int RecordOff = GuidLength;   

    internal const ushort ResultOk = 0;
 /// <summary>Result 7: no guild exists for the request, or the GUID is not the requester's guild.</summary>
    internal const ushort ResultGuildNotFound = 7;
 /// <summary>Result 17: the requester is not the guild master.</summary>
    internal const ushort ResultNotMaster = 17;
 /// <summary>Result 20: another rank index already uses the requested title.</summary>
    internal const ushort ResultDuplicateName = 20;
 /// <summary>Result 21: the rank index is outside the range 0 to 9.</summary>
    internal const ushort ResultOutOfRange = 21;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RecordOff + GuildRosterPackets.RankRecordLen) return;
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        
        var emblems = services.GetRequiredService<IGuildEmblemRepository>();
        await emblems.InitAsync();

        var span = payload.Span;
        byte index = span[RecordOff];
        
        
        string name = GuildPackets.ReadWideNameAt(span, RecordOff + 1, GuildRosterPackets.RankNameMaxChars);
        byte permissions = span[RecordOff + GuildRosterPackets.RankRecordLen - 1];

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        ushort result;
        GuildRank? rank = null;
        if (guild == null || !GuidMatches(guild, span[..GuidLength])) result = ResultGuildNotFound;
        else
        {
            (var refusal, rank) = await GuildService.Instance.ModifyRankAsync(guild, player.CharacterId, index, name, permissions);
            result = ResultOf(refusal);
        }

        Logger.LogInformation("Session {Id}: {Name} sets guild title {Index} '{Title}' perms 0x{Perms:X2} -> result {Result}",
            session.SessionId, player.Name, index, name, permissions, result);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildModifyAuthority, (ReadOnlyMemory<byte>)BuildAnsGuildModifyAuthority2633(result));

        
        if (result != ResultOk || guild == null || rank == null) return;

        await GuildPackets.BroadcastToGuildAsync(guild, PlayerManager, Broadcast, ZoneS2COpcodes.NfyGuildModifyAuthority,
            GuildRosterPackets.BuildRankRecord(rank));

        
        
        
        
        
        
        
        
        
        
        
        
        foreach (var holderCharId in GuildService.HoldersOf(guild, rank.Index))
        {
            if (PlayerManager.GetPlayerByCharacterId(holderCharId) is not { } holder) continue;
            if (Broadcast.GetSession(holder.SessionId) is not { } holderSession) continue;
            await PcViewInfoPackets.BroadcastToViewersAsync(holder, guild, holderSession, Broadcast, emblems);
        }
    }

    private static bool GuidMatches(Guild g, ReadOnlySpan<byte> guid)
    {
        lock (g.Gate) return g.Guid16 != null && guid.SequenceEqual(g.Guid16);
    }

 /// <summary>The 2633 code for a <see cref="GuildService.ModifyRankAsync"/> outcome.</summary>
    internal static ushort ResultOf(GuildService.ModifyRankRefusal refusal) => refusal switch
    {
        GuildService.ModifyRankRefusal.None => ResultOk,
        GuildService.ModifyRankRefusal.NotMaster => ResultNotMaster,
        GuildService.ModifyRankRefusal.OutOfRange => ResultOutOfRange,
        GuildService.ModifyRankRefusal.DuplicateName => ResultDuplicateName,
        _ => ResultGuildNotFound,   
    };

 /// <summary>Builds the 2633 AnsGuildModifyAuthority body: [u16 result].</summary>
    internal static byte[] BuildAnsGuildModifyAuthority2633(ushort result) => [(byte)result, (byte)(result >> 8)];
}
