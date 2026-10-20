using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1512 ReqGuildSkillLearn: the guild master buys guild skill levels with the guild's skill points. The learner receives a 2722 AnsGuildSkillLearn, and on success every online member receives a 2724 NfyGuildSkillLearn.
/// </summary>
/// <remarks>
/// <para>
/// The request is a u16-counted list of Guild_Skill_IDs, one per level bought. Each level is checked against the guild's level and skill points, and a request that cannot be learned in full learns nothing. The wire format and rules are described in <see cref="GuildSkillLearnPackets"/>.
/// </para>
/// <para>
/// The rank check comes first: a requester who is not the guild master is refused with code 19 (not master), and no 2724 is sent. Other refusal codes are 7 (no guild), 47 (not enough skill points) and 48 (invalid skill index). A success sends the 2724 to every online member, the learner included, so the guild skill list updates for all of them.
/// </para>
/// </remarks>
public class GuildSkillLearnHandler : ZoneRmiHandlerBase<GuildSkillLearnHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildSkillLearn;
    public override string Name => "GuildSkillLearn";
    public GuildSkillLearnHandler(IServiceScopeFactory s, ILogger<GuildSkillLearnHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        var parsed = GuildSkillLearnPackets.TryParseRequest(payload.Span, out var requested);

        
        var gate = GuildSkillLearnPackets.GateResult(guild, player.CharacterId);
        if (gate != GuildSkillLearnPackets.ResultLearned || guild is null)
        {
            Logger.LogInformation("{Name} asked to learn guild skills [{Ids}]: refused {Code} (guild {Guild}); nothing learned",
                player.Name, string.Join(",", requested), gate, guild?.Id);
            await SendResultAsync(session, gate, ct);
            return;
        }

        
        if (!parsed)
        {
            Logger.LogWarning("{Name} sent an unparseable ReqGuildSkillLearn ({Len} bytes)", player.Name, payload.Length);
            await SendResultAsync(session, GuildSkillLearnPackets.ResultWrongSkillIndex, ct);
            return;
        }

        var store = services.GetRequiredService<GameDataStore>();
        var table = GuildSkillLearnPackets.Index(store.GuildSkillLevel);
        var guildLevel = GuildRosterPackets.GuildLevelOf(guild.Exp, store.GuildExpTable).Level;

        
        
        List<uint>? learned;
        GuildSkillLearnPackets.LearnRefusal refusal;
        byte unspentAfter;
        lock (guild.Gate)
        {
            learned = GuildSkillLearnPackets.PlanLearn(requested, guild.Skills, guildLevel, table, out refusal);
            if (learned is not null)
                foreach (var id in learned)
                    if (!guild.Skills.Contains(id)) guild.Skills.Add(id);
            unspentAfter = GuildSkillLearnPackets.UnspentPoints(guildLevel, guild.Skills, table);
        }

        if (learned is null)
        {
            var code = GuildSkillLearnPackets.ResultCodeOf(refusal);
            Logger.LogInformation("{Name}: guild {Guild} cannot learn [{Ids}] ({Refusal}, level {Level}, {Points} points): 2722 {Code}, nothing learned",
                player.Name, guild.Id, string.Join(",", requested), refusal, guildLevel, unspentAfter, code);
            await SendResultAsync(session, code, ct);
            return;
        }

        
        await GuildService.Instance.AddSkillAsync(guild, learned[^1]);

        
        await SendResultAsync(session, GuildSkillLearnPackets.ResultLearned, ct);

        var notice = GuildSkillLearnPackets.BuildNfyGuildSkillLearn2724(unspentAfter,
            GuildSkillLearnPackets.TopIdPerLine(learned));
        await GuildPackets.BroadcastToGuildAsync(guild, PlayerManager, Broadcast, ZoneS2COpcodes.NfyGuildSkillLearn, notice);

        Logger.LogInformation("{Name}: guild {Guild} learned [{Ids}], {Points} points left",
            player.Name, guild.Id, string.Join(",", learned), unspentAfter);
    }

 /// <summary>Sends the 2722 AnsGuildSkillLearn body: [u16 result].</summary>
    private static Task SendResultAsync(ProudSession session, ushort result, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSkillLearn, writer => writer.WriteUInt16(result), ct);
}
