using System.Globalization;
using RO2.GameData;
using RO2.Guilds.Application;
using RO2.Guilds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>Host adapter of <see cref="IGuildNotifier"/> over the existing guild broadcast helper.</summary>
public sealed class GuildNotifier(PlayerStateManager players, BroadcastService broadcast) : IGuildNotifier
{
    public Task BroadcastAsync(Guild guild, ushort opcode, byte[] body, uint? exceptCharId = null)
        => GuildPackets.BroadcastToGuildAsync(guild, players, broadcast, opcode, body, exceptCharId);
}

/// <summary>Host adapter of <see cref="IGuildMemberLocator"/> over the player registry.</summary>
public sealed class GuildMemberLocator(PlayerStateManager players) : IGuildMemberLocator
{
    public bool IsOnline(uint charId) => GuildRosterPackets.IsOnline(players, charId);

    public uint? SessionIdOf(uint charId) => players.GetPlayerByCharacterId(charId)?.SessionId;

    public IReadOnlyList<uint> OnlineMemberCharIds(Guild guild)
    {
        uint[] ids;
        lock (guild.Gate) ids = guild.Members.Select(m => m.CharId).ToArray();
        return ids.Where(id => IsOnline(id)).ToArray();
    }
}

/// <summary>Host adapter of <see cref="IGuildConfig"/> over the loaded Config_Def table.</summary>
public sealed class GuildConfig(GameDataStore data) : IGuildConfig
{
    private const ushort ConfigGuildCreateFee = 2;

    public long CreateFee
    {
        get
        {
            var row = data.ConfigDef.FirstOrDefault(c => c.type == ConfigGuildCreateFee);
            return row != null && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fee) && fee > 0
                ? fee : 0;
        }
    }

    public TimeSpan RejoinCooldown => GuildInviteRules.RejoinCooldown(data.ConfigDef);

    public TimeSpan InviteLifetime => GuildInviteRules.InviteLifetime(data.ConfigDef);

    public long FundsLimit(Guild guild)
    {
        lock (guild.Gate)
            return GuildFundsInHandPackets.StorageLimit(guild.Skills, GuildSkillLearnPackets.Index(data.GuildSkillLevel));
    }

    public int HistoryRetentionDays => GuildStorageLog.HistoryRetentionDays(data.ConfigDef);

    public int HistoryPageSize => GuildStorageLog.HistoryPageSize(data.ConfigDef);
}

/// <summary>Host adapter of <see cref="IGuildDirectory"/> over the live registry singleton.</summary>
public sealed class GuildDirectory : IGuildDirectory
{
    public IReadOnlyList<uint> GetGuildMemberCharIds(uint guildId)
    {
        var g = GuildService.Instance.GetById(guildId);
        if (g == null) return Array.Empty<uint>();
        lock (g.Gate) return g.Members.Select(m => m.CharId).ToArray();
    }

    public Guild? GetByCharId(uint charId) => GuildService.Instance.GetByCharId(charId);
}

/// <summary>Host adapter of <see cref="IGuildExpLedger"/>: the live registry credits under the guild's gate and writes the guild.</summary>
public sealed class GuildExpLedger : IGuildExpLedger
{
    public Task<(ulong Before, ulong After)> CreditMemberAsync(Guild guild, uint charId, ulong amount)
        => GuildService.Instance.CreditMemberExpAsync(guild, charId, amount);
}

/// <summary>Host adapter of <see cref="IGuildSkillLedger"/>: the live registry adds the skill under the guild's gate and writes the guild once.</summary>
public sealed class GuildSkillLedger : IGuildSkillLedger
{
    public Task AddSkillAsync(Guild guild, uint skillId) => GuildService.Instance.AddSkillAsync(guild, skillId);
}

/// <summary>Host adapter of <see cref="IGuildNoticeLedger"/>: the live registry sets the notice and writes the guild once.</summary>
public sealed class GuildNoticeLedger : IGuildNoticeLedger
{
    public Task SetNoticeAsync(Guild guild, string notice) => GuildService.Instance.SetNoticeAsync(guild, notice);
}

/// <summary>Host adapter of <see cref="IGuildFundsLedger"/> over the live registry singleton.</summary>
public sealed class GuildFundsLedger : IGuildFundsLedger
{
    public Task<(FundsRefusal Refusal, long FundsAfter)> AddAsync(Guild guild, long amount, long limit)
        => GuildService.Instance.AddFundsAsync(guild, amount, limit);

    public Task<(FundsRefusal Refusal, long FundsAfter)> TakeAsync(Guild guild, long amount)
        => GuildService.Instance.TakeFundsAsync(guild, amount);

    public Task<long> ReturnAsync(Guild guild, long amount) => GuildService.Instance.ReturnFundsAsync(guild, amount);
}
