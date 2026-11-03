using RO2.DungeonFinder.Domain;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>Turns the dungeon finder's typed effects into the frames the client reads.</summary>
internal static class FinderEffectTranslator
{
    public static DungeonFinderPartyRound.Outgoing ToFrame(FinderEffect e) => e switch
    {
        SearchAnswered a => SearchFrame(a),
        SelectRolePlease p => Frame(p.SessionId, ZoneS2COpcodes.NfyFindParty_Party_Dungeon_SelectRolePlease,
            FindPartyRolePackets.SelectRolePlease2822.ToArray()),
        // The 2806 builder writes healers, dealers, tanks; the named arguments keep each count in its own slot.
        InProgress i => Frame(i.SessionId, ZoneS2COpcodes.AnsPartyMatchingInProgressInfo,
            DungeonFinderPackets.BuildInProgressInfo2806(i.ListId,
                new DungeonFinderQueue.RoleCounts(Tanks: i.Tanks, Healers: i.Healers, Dealers: i.Dealers), i.OwnRole)),
        SelectRoleAnswered a => Frame(a.SessionId, ZoneS2COpcodes.AnsFindParty_Party_SelectRole,
            FindPartyRolePackets.BuildSelectRoleAnswer2834(a.Result)),
        RoundFailed f => Frame(f.SessionId, ZoneS2COpcodes.NfyResult_FindParty_Party_SelectRole,
            FindPartyRolePackets.BuildRoundFailed2831()),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e, null),
    };

    private static DungeonFinderPartyRound.Outgoing SearchFrame(SearchAnswered a)
    {
        var opcode = a.Kind == SearchKind.Random
            ? ZoneS2COpcodes.AnsPartyMatchingFindDungeonRandom
            : ZoneS2COpcodes.AnsPartyMatchingFindDungeon;
        return Frame(a.SessionId, opcode, DungeonFinderPartyRound.SearcherAnswer(opcode, a.Result));
    }

    private static DungeonFinderPartyRound.Outgoing Frame(uint sessionId, ushort opcode, byte[] body)
        => new(sessionId, opcode, body);
}
