using RO2.Parties.Domain;

namespace RO2.Parties.Application;

/// <summary>The current UTC instant, a port so the invite window and the lock lifetime can be driven by a test clock.</summary>
public interface IClock
{
    /// <summary>The current UTC instant.</summary>
    DateTime UtcNow { get; }
}

/// <summary>The guild name a character shows in a party member record (null when it has no guild).</summary>
public interface IGuildNameLookup
{
    /// <summary>The name of the guild the character belongs to, or null.</summary>
    string? GuildNameOf(uint characterId);
}

/// <summary>What a party member record shows of a character. The host builds it from the live character.</summary>
public sealed record MemberView(ulong Gid, string Name, byte Level, byte Job, uint Hp, uint MaxHp, uint Mp, uint MaxMp,
    uint MapId, float X, float Y, float Z, byte Profession, byte ProJobLevel, byte ProJobId, string? GuildName, bool Online);

/// <summary>Who is in the world and what they look like, by character GID.</summary>
public interface IMemberDirectory
{
    /// <summary>Whether the character is bound to a live in-world session.</summary>
    bool IsOnline(ulong gid);

    /// <summary>The character's record view (live while online, otherwise its last known state), or null when unknown.</summary>
    MemberView? Snapshot(ulong gid);
}

/// <summary>
/// Delivers a decision's effects. The host implements it over its sessions and wire builders; each call is one send
/// the aggregate decided on, and they are made in the order the decision listed them.
/// </summary>
public interface IPartyNotifier
{
    /// <summary>Sends one message to one member.</summary>
    Task SendToMemberAsync(ulong gid, PartyMessage message);

    /// <summary>To every online member of the party as it stands now, except <paramref name="exceptGid"/> when set.</summary>
    Task SendToPartyAsync(PartyMessage message, ulong? exceptGid);

    /// <summary>The roster (2357 when <paramref name="create"/>, else 2361): to every online member, or only to <paramref name="onlyGid"/>.</summary>
    Task SendRosterAsync(bool create, ulong? onlyGid);

    /// <summary>The 2359 delta for the joiner, to every online member except the joiner.</summary>
    Task SendJoinDeltaAsync(ulong joinerGid);

    /// <summary>The 2358 destroy, <paramref name="copies"/> times to each recipient.</summary>
    Task SendDestroyAsync(int copies, IReadOnlyList<ulong> recipients);

    /// <summary>The 2367 layoff to a kicked member.</summary>
    Task SendLayoffAsync(ulong gid);

    /// <summary>A member was locked: make sure the expiry sweep is running.</summary>
    void ScheduleLockSweep();
}
