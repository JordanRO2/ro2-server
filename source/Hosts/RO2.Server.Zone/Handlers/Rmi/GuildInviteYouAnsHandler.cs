using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1430 AnsGuildInviteYou: the invitee's answer to a guild invite.
/// </summary>
/// <remarks>
/// The body is 33 bytes: [16-byte guild GUID][16-byte invite token][u8 accept]. The answer only counts against the invite the character currently holds, matched by guild GUID and by the token of the 2566 that carried it. An accept adds the character to the guild and notifies online members with a 2573 NfyGuildMemberAdd. The new member then loads the guild info and roster (1424 and 1425).
/// </remarks>
public class GuildInviteYouAnsHandler : ZoneRmiHandlerBase<GuildInviteYouAnsHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.AnsGuildInviteYou;
    public override string Name => "GuildInviteYouAns";
    public GuildInviteYouAnsHandler(IServiceScopeFactory s, ILogger<GuildInviteYouAnsHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    private const int AnswerLen = 33;

 /// <summary>Result 0: the answer was taken. An accept joins the guild; a decline is reported to the inviter.</summary>
    internal const ushort AnswerTaken = 0;

 /// <summary>Result 1: the accepting character is already a member of a guild.</summary>
    internal const ushort AlreadyInAGuild = 1;

 /// <summary>Result 12: no valid invite matches the answer, including an answer given after the invite has expired.</summary>
    internal const ushort NoInvite = 12;

 /// <summary>Config type identifier for the invite answer timeout, in seconds.</summary>
    private const ushort ConfigInviteAnswerSeconds = 5;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < AnswerLen) return;
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);

        var span = payload.Span;
        var accepted = span[32] != 0;
        var answer = span[..AnswerLen].ToArray();

        var invite = GuildService.Instance.TakeInvite(player.CharacterId);
        
        var now = DateTime.UtcNow;
        var lifetime = AnswerLifetime(services.GetRequiredService<GameDataStore>().ConfigDef);
        if (invite != null && IsExpired(invite, lifetime, now))
        {
            Logger.LogInformation("{Name} answers a guild invite {Age:F0} s old, past its {Lifetime} s lifetime",
                player.Name, (now - invite.CreatedUtc).TotalSeconds, lifetime?.TotalSeconds);
            invite = null;
        }
        var guild = invite != null ? GuildService.Instance.GetById(invite.GuildId) : null;
        
        
        
        if (guild != null && (!guild.Guid16.AsSpan().SequenceEqual(span.Slice(0, 16))
                              || !invite!.Token.AsSpan().SequenceEqual(span.Slice(16, 16))))
            guild = null;

        Logger.LogInformation("{Name} answers guild invite accepted={Accepted} guild={GuildId}",
            player.Name, accepted, guild?.Id ?? 0);

        if (accepted && guild != null && GuildService.Instance.GetByCharId(player.CharacterId) == null)
        {
            var member = await GuildService.Instance.AddMemberAsync(guild, player.CharacterId, player.Name, player.Level, player.Job);

            await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildInviteYouAns, (ReadOnlyMemory<byte>)BuildAnsGuildInviteYouAns(AnswerTaken, answer));

            
            
            
            
            
            
            
            var characters = services.GetRequiredService<ICharacterRepository>();
            var memberView = await GuildRosterPackets.ResolveMemberAsync(member, PlayerManager, characters);
            var addBody = GuildRosterPackets.BuildNfyGuildMemberAdd(guild, memberView);

            
            
            await NotifyInviterAsync(invite!, answer, player.Name);
            await GuildPackets.BroadcastToGuildAsync(guild, PlayerManager, Broadcast, ZoneS2COpcodes.NfyGuildMemberAdd, addBody);

            
            
            await KharaService.OnGuildJoinedAsync(session, player, services, Logger, ct);

            
            
            
            
            
            
            
            return;
        }

        
        
        
        ushort result = RefusalCode(accepted, guild != null);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildInviteYouAns, (ReadOnlyMemory<byte>)BuildAnsGuildInviteYouAns(result, answer));

        
        
        
        
        if (!accepted && guild != null)
            await NotifyInviterAsync(invite!, answer, player.Name);
    }

 /// <summary>
 /// Sends the 2567 NfyGuildInviteMemberAns to the inviter. The guild GUID comes from the answer, already checked against the inviting guild. The name is the answering character's name (<see cref="PlayerState.Name"/>). An offline inviter receives nothing.
 /// </summary>
    private async Task NotifyInviterAsync(GuildPendingInvite invite, byte[] answer33, string inviteeName)
    {
        var body = GuildInviteAnswerPackets.BuildNfyGuildInviteMemberAns(answer33.AsSpan(0, 16), answer33[32], inviteeName);
        var inviter = PlayerManager.GetPlayerByCharacterId(invite.InviterCharId);
        if (inviter == null) return;
        var inviterSession = Broadcast.GetSession(inviter.SessionId);
        if (inviterSession == null) return;
        await inviterSession.SendRmiAsync(ZoneS2COpcodes.NfyGuildInviteMemberAns, (ReadOnlyMemory<byte>)body);
    }

 /// <summary>
 /// Returns the 2568 result code for an answer that did not join: <see cref="AnswerTaken"/> for a decline of a valid invite, <see cref="AlreadyInAGuild"/> for an accept by a guild member, and <see cref="NoInvite"/> when no valid invite matches.
 /// </summary>
    internal static ushort RefusalCode(bool accepted, bool validInvite)
        => !validInvite ? NoInvite : accepted ? AlreadyInAGuild : AnswerTaken;

 /// <summary>
 /// Returns how long an invite can be answered, read from the invite answer timeout config row. Returns null (no limit) when the row is absent or not a positive number.
 /// </summary>
    internal static TimeSpan? AnswerLifetime(IReadOnlyList<ConfigDef> config)
    {
        var row = config.FirstOrDefault(c => c.type == ConfigInviteAnswerSeconds);
        return row != null && double.TryParse(row.Value, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : null;
    }

 /// <summary>
 /// Returns true when <paramref name="invite"/> is past <paramref name="lifetime"/> at <paramref name="nowUtc"/>. Never expires when <paramref name="lifetime"/> is null. An invite is expired at exactly its lifetime.
 /// </summary>
    internal static bool IsExpired(GuildPendingInvite invite, TimeSpan? lifetime, DateTime nowUtc)
        => lifetime is { } l && nowUtc - invite.CreatedUtc >= l;

 /// <summary>
 /// Builds the 2568 AnsGuildInviteYouAns body: [u16 result][the 33-byte 1430 answer echoed]. The 35-byte layout is the one the client reads.
 /// </summary>
    internal static byte[] BuildAnsGuildInviteYouAns(ushort result, byte[] answer33)
    {
        var body = new byte[2 + AnswerLen];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        answer33.AsSpan(0, AnswerLen).CopyTo(body.AsSpan(2));
        return body;
    }
}
