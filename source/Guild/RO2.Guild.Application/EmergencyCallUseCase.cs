using RO2.GameData.Tables;
using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>An online guild member an Emergency Call may prompt, with the session id the host resolves the connection from.</summary>
public readonly record struct EmergencyCallRecipient(uint CharId, uint SessionId);

/// <summary>The verdict of a landed Emergency Call: the guild it belongs to and, when allowed, the members to prompt.</summary>
public sealed record EmergencyCallPlan(EmergencyCallRefusal Refusal, Guild? Guild, IReadOnlyList<EmergencyCallRecipient> Recipients);

/// <summary>
/// Decides a landed guild Emergency Call: the <see cref="EmergencyCallRules.Validate"/> verdict, then the recipients (guild
/// members other than the caster whom the locator reports connected). It sends nothing and holds no state; the host records
/// the call, checks each session and sends the 2960 prompts.
/// </summary>
public sealed class EmergencyCallUseCase(IGuildDirectory directory, IGuildMemberLocator locator)
{
    /// <summary>The verdict for the caster's landed call and, when allowed, the members to prompt.</summary>
    public EmergencyCallPlan Plan(uint casterCharId, uint skillId, uint casterMapId,
        IReadOnlySet<uint> targetMaps, IReadOnlyList<GuildSkillLevel> guildSkillLevels)
    {
        var guild = directory.GetByCharId(casterCharId);
        var refusal = EmergencyCallRules.Validate(guild, casterCharId, skillId, casterMapId, targetMaps, guildSkillLevels);
        if (refusal != EmergencyCallRefusal.None || guild is null)
            return new EmergencyCallPlan(refusal, guild, []);

        uint[] memberIds;
        lock (guild.Gate) memberIds = guild.Members.Select(m => m.CharId).ToArray();

        var recipients = new List<EmergencyCallRecipient>();
        foreach (var charId in memberIds)
            if (charId != casterCharId && locator.SessionIdOf(charId) is { } sessionId)
                recipients.Add(new EmergencyCallRecipient(charId, sessionId));
        return new EmergencyCallPlan(EmergencyCallRefusal.None, guild, recipients);
    }
}
