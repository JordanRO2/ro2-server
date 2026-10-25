using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1429 ReqGuildInviteMember: invites a player to the guild by name.
/// </summary>
/// <remarks>
/// The request carries the invitee name (UTF-16, NUL-terminated, up to 22 characters). The inviter receives a 2565 result. On success the invitee also receives a 2566 invitation popup; the inviter receives the 2567 answer once the invitee responds. A refusal sends no 2566 and no 2567. The result code is decided by <c>GuildInviteRules</c>.
/// </remarks>
public class GuildInviteHandler : ZoneRmiHandlerBase<GuildInviteHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildInviteMember;
    public override string Name => "GuildInvite";

    private const int NameFieldOff = 4;
    private const int NameMaxChars = 22;                 

    public GuildInviteHandler(IServiceScopeFactory s, ILogger<GuildInviteHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        var departures = services.GetRequiredService<IGuildDepartureRepository>();
        await departures.InitAsync();
        var config = services.GetRequiredService<GameDataStore>().ConfigDef;

        var span = payload.Span;
        var inviteeName = span.Length >= 6 ? GuildPackets.ReadWideNameAt(span, NameFieldOff, NameMaxChars) : string.Empty;
        var now = DateTime.UtcNow;
        var lifetime = GuildInviteRules.InviteLifetime(config);

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        var mayInvite = guild != null && GuildService.HasPermission(guild, player.CharacterId, GuildService.PermissionInvite);
        var invitee = string.IsNullOrWhiteSpace(inviteeName) ? null : PlayerManager.GetPlayerByName(inviteeName);
        
        
        var nameExists = invitee != null
                         || (mayInvite && await GuildInviteRules.NameExistsAsync(services.GetRequiredService<ICharacterRepository>(), inviteeName));
        var facts = new GuildInviteRules.InviteFacts(
            InviterInGuild: guild != null,
            InviterMayInvite: mayInvite,
            NameExists: nameExists,
            InviteeOnline: invitee != null,
            InviteeInGuild: invitee != null && GuildService.Instance.GetByCharId(invitee.CharacterId) != null,
            RejoinAtUtc: invitee == null ? null : GuildInviteRules.RejoinAtUtc(
                departures.LeftUtcOf(invitee.CharacterId), GuildInviteRules.RejoinCooldown(config), now),
            InviteePending: invitee != null && GuildInviteRules.IsPending(
                GuildService.Instance.PeekInvite(invitee.CharacterId), lifetime, now));
        var (result, rejoinAtUtc) = GuildInviteRules.Decide(facts);

        
        
        
        
        byte[]? inviteToken = null;
        if (result == GuildInviteRules.Sent)
        {
            inviteToken = Guid.NewGuid().ToByteArray();
            if (!GuildService.Instance.TryAddInvite(invitee!.CharacterId, guild!.Id, player.CharacterId, inviteToken, now, lifetime))
                result = GuildInviteRules.AlreadyInvited;
        }

        Logger.LogInformation("Session {Id}: {Name} invites '{Invitee}' -> result {Result}{Until}",
            session.SessionId, player.Name, inviteeName, result, rejoinAtUtc is { } until ? $" (rejoin at {until:u})" : "");

        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildInviteMember,
            (ReadOnlyMemory<byte>)GuildPackets.BuildAnsGuildInviteMember(result, inviteeName, rejoinAtUtc?.ToLocalTime()));

        if (result != GuildInviteRules.Sent || inviteToken == null) return;
        if (Broadcast.GetSession(invitee!.SessionId) is { } inviteeSession)
        {
            
            await inviteeSession.SendRmiAsync(ZoneS2COpcodes.NfyGuildInviteYouReq,
                (ReadOnlyMemory<byte>)GuildPackets.BuildNfyGuildInviteYouReq(guild!, inviteToken, player.Name, inviteeName));
        }
    }
}
