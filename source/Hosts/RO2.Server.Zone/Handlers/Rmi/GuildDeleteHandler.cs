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
/// RMI 1427 ReqGuildDelete — disband the guild (master only), sent when the master confirms the dialog a 2688 0
/// opened. Removes the guild document and clears
/// every member's membership.
/// C2S (NONE, 16 B): the guild GUID. A successful disband is answered, all UNENC, with
/// <c>2472, 2488, 2472, 2488</c> (GuildSkillBarRefresh), then the 18-byte 2562 [u16 0][16 GUID of the request],
/// then the player's own 2591 NfyPCViewInfoUpdate in its guildless form. The client asks for
/// nothing afterwards (no 1424 follows), so that 2591 is what takes the guild off the player's view.
/// Refused unless GuildDelPossibleHandler.DisbandRefusal allows it at the moment of the delete.
/// </summary>
public class GuildDeleteHandler : ZoneRmiHandlerBase<GuildDeleteHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildDelete;
    public override string Name => "GuildDelete";

    /// <summary>2562 result code for success: the guild was disbanded.</summary>
    private const ushort ResultOk = 0;

    /// <summary>
    /// 2562 code for a vault that still holds items or zeny: guild result code 41 ->
    /// string_ui 7861 "Cannot disband a guild if the guild storage has Zeny or items in it.".
    /// </summary>
    public const ushort ResultStorageNotEmpty = 41;

    /// <summary>Size in bytes of the guild GUID: the whole 1427 body, echoed back in the 2562.</summary>
    private const int GuidLength = 16;

    public GuildDeleteHandler(IServiceScopeFactory s, ILogger<GuildDeleteHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);
        var emblems = services.GetRequiredService<IGuildEmblemRepository>();

        // The same rules 1495 answers (master only, no other member, empty vault), re-checked here and
        // applied atomically with the unlink: 1495 is only the client's precheck, seconds before this, and a
        // 1427 can also arrive without it (or after a deposit made in between). DisbandRefusal gives 2688 codes;
        // AnsGuildDeleteResult turns them into the 2562's.
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        var refusal = guild == null
            ? GuildDelPossibleHandler.DisbandRefusal(null, player.CharacterId)
            : await GuildService.Instance.TryDisbandAsync(guild, g => GuildDelPossibleHandler.DisbandRefusal(g, player.CharacterId));
        var result = AnsGuildDeleteResult(refusal);

        Logger.LogInformation("Session {Id}: {Name} deletes guild -> result {Result}", session.SessionId, player.Name, result);

        // Preamble to a successful 2562: the stored quick bar and the usable-skill list, twice. It runs
        // after TryDisbandAsync has taken the character out of the guild index, so the 2488 carries the
        // character's own skills without the dissolved guild's line skills.
        if (result == ResultOk)
            await GuildSkillBarRefresh.SendAsync(session, player, services, Logger);

        // 2562 AnsGuildDelete (NONE, Blob<18>): [u16 result][16-byte GUID echoed from the request]. The echo
        // matters: the client shows the "guild was disbanded" message only when it equals the player's guild GUID.
        var guid = new byte[GuidLength];
        payload.Span[..Math.Min(GuidLength, payload.Length)].CopyTo(guid);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildDelete, writer =>
        {
            writer.WriteUInt16(result);
            writer.WriteBytes(guid);
        });

        if (result == ResultOk && guild != null)
        {
            // The disbander's guildless 2591, to them and to the clients that can see them — the same
            // per-PC view update a leave sends (see PcViewInfoPackets.BroadcastToViewersAsync). The rules
            // above leave the master as the only member, so no other member needs one. Sent before the
            // emblem cleanup (a guildless 2591 does not read the emblem store).
            await PcViewInfoPackets.BroadcastToViewersAsync(player, null, session, Broadcast, emblems);

            await emblems.InitAsync();
            await emblems.RemoveAsync(guild.Guid16);
        }
    }

    /// <summary>
    /// The 2562 code for a <see cref="GuildDelPossibleHandler.DisbandRefusal"/> result (a 2688 code). The 2562
    /// consumer has no switch of its own: any non-zero code goes to
    /// the client's guild result handler. There 7 reads string_etc 2000000068 "Cannot Find Guild", 9 reads 7860
    /// "Cannot disband a guild with members." and 19 reads 8864 "Only Guild Master or the guild member have the
    /// authority to use.", so those three pass through; but 45 (the 2688's vault code) reads 7676 "Used a
    /// prohibited word." (cases 42-45) and 41 is the case that reads 7861, so the vault refusal becomes 41. This
    /// mapping follows the client's message table.
    /// </summary>
    internal static ushort AnsGuildDeleteResult(ushort disbandRefusal)
        => disbandRefusal == GuildDelPossibleHandler.ResultStorageNotEmpty ? ResultStorageNotEmpty : disbandRefusal;
}
