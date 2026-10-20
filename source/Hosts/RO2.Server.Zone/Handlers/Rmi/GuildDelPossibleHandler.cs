using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1495 ReqGuildDelPossible — the client's "may I disband?" check before its disband confirmation.
/// C2S (NONE, 16B): [16 guildGUID]. The flow in every disband
/// is <c>1495 → 2688 0000</c> a fraction of a second later, then <c>1427 ReqGuildDelete</c> seconds after the 2688,
/// when the user confirms. The 0 answer is what opens the confirmation: the
/// client's consumer sends notify 65920 to its window on it. The client states the rules the answer enforces:
/// only the master may disband (string_ui 7859), not while the guild has members (7860), and not while the guild
/// storage holds zeny or items (7861). The old handler answered 0 to anyone, so a master could disband with
/// items in the vault — and 1427 deletes the guild document, vault included, so those items were destroyed.
/// </summary>
public class GuildDelPossibleHandler : ZoneRmiHandlerBase<GuildDelPossibleHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildDelPossible;
    public override string Name => "GuildDelPossible";
    public GuildDelPossibleHandler(IServiceScopeFactory s, ILogger<GuildDelPossibleHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    
    
    
    
    
    
    
    
    
    
    
    
    public const ushort ResultAllowed = 0;
    public const ushort ResultNoGuild = 7;
    public const ushort ResultNotMaster = 19;
    public const ushort ResultHasMembers = 9;
    public const ushort ResultStorageNotEmpty = 45;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        var result = DisbandRefusal(guild, player.CharacterId);

        Logger.LogInformation("Session {Id}: {Name} asks whether guild {Guild} can be disbanded -> {Result}",
            session.SessionId, player.Name, guild?.Name ?? "(none)", result);

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildDelPossible, writer => writer.WriteUInt16(result));
    }

 /// <summary>
 /// Why <paramref name="charId"/> may not disband <paramref name="g"/>, as a 2688 code, or
 /// <see cref="ResultAllowed"/>. The checks run in the order the client's own builder runs them (in a
 /// guild, master, no other member), then the vault. Public because the disband itself (1427,
 /// GuildDeleteHandler through GuildService.TryDisbandAsync) applies the same rules again under the guild's
 /// gate, to a request that skipped this check or that comes seconds after it.
 /// </summary>
    public static ushort DisbandRefusal(Guild? g, uint charId)
    {
        if (g == null) return ResultNoGuild;
        lock (g.Gate)
        {
            if (g.MasterCharId != charId) return ResultNotMaster;
            if (g.Members.Any(m => m.CharId != charId)) return ResultHasMembers;
            if (g.Storage.Count > 0 || g.Gold > 0) return ResultStorageNotEmpty;
        }
        return ResultAllowed;
    }
}
