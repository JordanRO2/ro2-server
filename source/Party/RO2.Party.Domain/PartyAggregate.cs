namespace RO2.Parties.Domain;

/// <summary>Why a member left. Each reason is announced to the remaining members differently.</summary>
public enum PartyLeaveReason { Left, Kicked }

/// <summary>
/// A party keyed by 64-bit character GID. Pure state plus the decision chains (<c>Decide*</c>): each mutates the
/// aggregate and returns its outcome with the effects to send, in send order. Everything external (who is online, the
/// time, member levels, map flags) comes in as an argument. A disbanded aggregate keeps its last member list so the
/// effects it returned can still be resolved against it.
/// </summary>
public sealed partial class PartyAggregate
{
    public const byte PartyTypeNormal = 0;
    public const byte PartyTypeRaid = 2;
    public const int NormalMaxMembers = 5;
    public const int RaidMaxMembers = 20;
    public const int MaxSubMasters = 3;
    public const byte DefaultItemRule = 2;
    public const byte DefaultItemQuality = 2;
    public const byte OutKindRemoved = 0;
    public const byte OutKindLocked = 1;
    public const int HelperLevelGap = 10;
    public const int DestroyCopiesOnDisband = 2;
    public const int DestroyCopiesAfterKick = 1;

    /// <summary>A locked member is dropped once it has been away this long.</summary>
    public static readonly TimeSpan LockedMemberLifetime = TimeSpan.FromSeconds(595);

    public const int CodePartyFull = 15707;
    public const int CodeInviterNotFound = 15708;
    public const int CodeTargetInParty = 15710;
    public const int CodeExpulsionNotMember = 15712;
    public const int CodeDeclined = 15715;
    public const int CodeTimedOut = 15716;
    public const int CodeAlreadyInvited = 15717;
    public const int CodeTypeChangeOnDungeonMap = 15719;
    public const int CodeChangeMasterWoeMap = 24324;

    public const ushort AnswerAccept = 0;
    public const ushort AnswerDecline = 1;
    public const ushort AnswerTimedOut = 2;

    private readonly List<ulong> _members = [];
    private readonly Dictionary<ulong, byte> _slots = [];
    private readonly Dictionary<ulong, byte> _helpers = [];
    private readonly HashSet<ulong> _lapsedHelpers = [];
    private readonly Dictionary<ulong, DateTime> _lockedSince = [];
    private readonly ulong[] _subMasters = new ulong[MaxSubMasters];

    public PartyAggregate(uint id, ulong founderGid, DateTime createdAt)
    {
        Id = id;
        LeaderGid = FounderGid = founderGid;
        CreatedAt = createdAt;
        AddMember(founderGid);
    }

    /// <summary>Rebuilds the aggregate from state the host holds (the members in roster order, the GID-keyed tables).</summary>
    public static PartyAggregate Restore(uint id, ulong founderGid, DateTime createdAt, ulong leaderGid, byte partyType,
        IEnumerable<ulong> members, IReadOnlyDictionary<ulong, byte> slots, IReadOnlyDictionary<ulong, byte> helpers,
        IEnumerable<ulong> lapsedHelpers, IReadOnlyDictionary<ulong, DateTime> lockedSince, IReadOnlyList<ulong> subMasters)
    {
        var party = new PartyAggregate(id, founderGid, createdAt) { LeaderGid = leaderGid, PartyType = partyType };
        party._members.Clear();
        party._slots.Clear();
        party._members.AddRange(members);
        foreach (var (gid, slot) in slots) party._slots[gid] = slot;
        foreach (var (gid, flag) in helpers) party._helpers[gid] = flag;
        party._lapsedHelpers.UnionWith(lapsedHelpers);
        foreach (var (gid, since) in lockedSince) party._lockedSince[gid] = since;
        for (var i = 0; i < party._subMasters.Length && i < subMasters.Count; i++) party._subMasters[i] = subMasters[i];
        return party;
    }

    public uint Id { get; }
    public ulong LeaderGid { get; private set; }
    public ulong FounderGid { get; }
    public DateTime CreatedAt { get; }
    public bool IsDisbanded { get; private set; }
    public byte PartyType { get; set; }
    public byte ItemRule { get; set; } = DefaultItemRule;
    public byte ItemQuality { get; set; } = DefaultItemQuality;
    public byte ExpRule { get; set; }
    public string Notice { get; set; } = string.Empty;

    public IReadOnlyList<ulong> Members => _members;
    public IReadOnlyDictionary<ulong, byte> FrameSlots => _slots;
    public IReadOnlyDictionary<ulong, byte> HelperFlags => _helpers;
    public IReadOnlyCollection<ulong> LapsedHelpers => _lapsedHelpers;
    public IReadOnlyDictionary<ulong, DateTime> LockedSince => _lockedSince;

    /// <summary>The sub-master GID table (3 slots, 0 = free), as stored.</summary>
    public IReadOnlyList<ulong> SubMasters => _subMasters;

    public int MaxMembers => PartyType == PartyTypeRaid ? RaidMaxMembers : NormalMaxMembers;
    public bool IsMember(ulong gid) => gid != 0 && _members.Contains(gid);
    public byte FrameSlotOf(ulong gid) => _slots.GetValueOrDefault(gid);

    /// <summary>The sub-master slots as the roster shows them: a GID that is no longer a member reads as free.</summary>
    public ulong[] SubMastersInRoster()
        => [.. _subMasters.Select(g => g != 0 && IsMember(g) ? g : 0UL)];

    /// <summary>Adds a member at the end of the roster in the lowest free frame slot.</summary>
    private void AddMember(ulong gid)
    {
        _slots[gid] = LowestFreeSlot();
        _members.Add(gid);
    }

    private byte LowestFreeSlot()
    {
        var used = new HashSet<byte>(_slots.Values);
        for (var slot = 1; slot <= byte.MaxValue; slot++)
            if (!used.Contains((byte)slot)) return (byte)slot;
        return 0;
    }

    private static PartyMessage Msg(PartyMessageKind kind, ulong gid = 0, int code = 0, string text = "", byte value = 0,
        ulong gid2 = 0, byte value2 = 0) => new(kind, gid, code, text, value, gid2, value2);

    private static (PartyOutcome, IReadOnlyList<PartyEffect>) Res(PartyResult result, int code = 0,
        params PartyEffect[] effects) => (new PartyOutcome(result, code), effects);

    private static (PartyOutcome, IReadOnlyList<PartyEffect>) Res(PartyOutcome outcome, params PartyEffect[] effects)
        => (outcome, effects);
}
