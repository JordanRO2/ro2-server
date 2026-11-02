using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>What the pre-debit checks of a guild creation say; the host maps it to the 2561 result code.</summary>
public enum CreateCheck
{
    /// <summary>Every check passed; the host takes the fee and registers the guild.</summary>
    Proceed,

    /// <summary>The requester is already in a guild.</summary>
    AlreadyInGuild,

    /// <summary>The name is empty or blank.</summary>
    NameBlank,

    /// <summary>The wallet holds less than the creation fee.</summary>
    NotEnoughZeny,
}

/// <summary>What the name gate decides for a creation, taken under the host's name gate.</summary>
public enum CreateRegistration
{
    /// <summary>Register the new guild.</summary>
    Register,

    /// <summary>The founder joined a guild meanwhile.</summary>
    AlreadyInGuild,

    /// <summary>A registered or reserved guild already has the name.</summary>
    NameTaken,
}

/// <summary>How a creation ended once the host tried to register it; every refusal hands the fee back.</summary>
public enum CreateSettlement
{
    /// <summary>The guild exists and the fee stays charged.</summary>
    Created,

    /// <summary>Refused for a taken name; the fee goes back.</summary>
    NameTakenRefund,

    /// <summary>Refused because the founder joined a guild meanwhile; the fee goes back.</summary>
    AlreadyInGuildRefund,
}

/// <summary>
/// The decisions of a guild creation: the checks before the fee is taken, the name-gate verdict, the refund verdict and
/// the founded guild. A thin decide: the host keeps the name gate, the wallet debit and refund, the notice, persistence and the wire.
/// </summary>
public static class GuildCreateUseCase
{
    /// <summary>Checks, in the original order: guild membership, blank name, wallet against fee. The host then takes the fee atomically.</summary>
    public static CreateCheck CheckRequest(bool requesterInGuild, string name, long gold, long fee)
    {
        if (requesterInGuild) return CreateCheck.AlreadyInGuild;
        if (string.IsNullOrWhiteSpace(name)) return CreateCheck.NameBlank;
        if (gold < fee) return CreateCheck.NotEnoughZeny;
        return CreateCheck.Proceed;
    }

    /// <summary>The name-gate verdict: membership is checked before the name.</summary>
    public static CreateRegistration DecideRegistration(bool founderInGuild, bool nameTaken)
        => founderInGuild ? CreateRegistration.AlreadyInGuild
         : nameTaken ? CreateRegistration.NameTaken
         : CreateRegistration.Register;

    /// <summary>Whether the fee stays charged or goes back, given what registration returned.</summary>
    public static CreateSettlement Settle(bool created, bool nameTaken)
        => nameTaken ? CreateSettlement.NameTakenRefund
         : !created ? CreateSettlement.AlreadyInGuildRefund
         : CreateSettlement.Created;

    /// <summary>The new guild: the founder as its only member and master on rank 0, a level 0 stored as the minimum, both stamped with <paramref name="createdUtc"/>.</summary>
    public static Guild Found(uint id, byte[] guid16, string name, uint masterCharId, ulong masterGid, string masterName, byte masterLevel, byte masterJob, DateTime createdUtc)
    {
        var g = new Guild
        {
            Id = id,
            Guid16 = guid16,
            Name = name,
            MasterCharId = masterCharId,
            Notice = string.Empty,
            CreatedUtc = createdUtc,
            Ranks = GuildRankRules.DefaultRanks(),
        };
        g.Members.Add(new GuildMember
        {
            CharId = masterCharId, Gid = masterGid, Name = masterName,
            Level = masterLevel == 0 ? GuildMember.MinLevel : masterLevel, Job = masterJob, AuthId = GuildRankRules.MasterRankIndex,
            JoinedUtc = createdUtc,
        });
        return g;
    }
}
