using RO2.Network.Protocol;
using RO2.ReadyChecks.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>Turns a ready check's effects into the 2697, 2698, 2699 and 2700 frames. The Domain knows no opcodes.</summary>
internal static class PartyReadyCheckFrames
{
    /// <summary>The opcode and body for an effect, or null for one that sends nothing.</summary>
    internal static (ushort Opcode, byte[] Body)? ToFrame(ReadyCheckEffect effect, ulong initiatorGid) => effect switch
    {
        AckOk => (ZoneS2COpcodes.AnsPartyFightingTrim,
            PartyReadyCheckPackets.BuildAnsPartyFightingTrim2698(PartyReadyCheckPackets.ResultOk)),
        AlreadyChecking => (ZoneS2COpcodes.AnsPartyFightingTrim,
            PartyReadyCheckPackets.BuildAnsPartyFightingTrim2698(PartyReadyCheckPackets.ResultAlreadyChecking)),
        AlreadyVoted => (ZoneS2COpcodes.AnsPartyFightingTrimVote,
            PartyReadyCheckPackets.BuildAnsPartyFightingTrimVote2700(PartyReadyCheckPackets.ResultAlreadyVoted)),
        NotifyStart => (ZoneS2COpcodes.NfyPartyFightingTrimStart,
            PartyReadyCheckPackets.BuildNfyPartyFightingTrimStart2699(initiatorGid)),
        CheckResult r => (ZoneS2COpcodes.NfyPartyFightingTrimResult,
            PartyReadyCheckPackets.BuildNfyPartyFightingTrimResult2697(r.Ready, r.NotReady, r.NotReadyNames)),
        _ => null,
    };
}
