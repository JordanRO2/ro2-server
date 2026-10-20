using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gate for the 1509 refusal pair: RMI 2717 AnsSkillError, sent unicast to the caster, and
/// RMI 2963 NfySkillDamageError, broadcast to the map.
/// </summary>
/// <remarks>
/// The reference payloads come from a 1509 for skill 630310 aimed at an NPC that had just died. The server answers
/// with 2717 and then 2963, and sends no 2718.
/// </remarks>
public class SkillDamageError2963Tests
{
 /// <summary>Reference 2717: code 24310, skill 630310.</summary>
    private const string Retail2717Hex = "f65e269e0900";

 /// <summary>Reference 2963: selfLen 18, msgTag 58499, zero, code 24310, skill 630310, caster with the PC tag.</summary>
    private const string Retail2963Hex = "120083e400000000f65e269e0900115e490b";

    [Fact]
    public void Error2963ReproducesARealRetailFrameByteForByte()
    {
        var built = SkillDamageErrorPackets.BuildSkillDamageError2963(
            SkillDamageErrorPackets.ResultPrimaryTargetDead, 630310, 0x0B495E11);

        Assert.Equal(Convert.FromHexString(Retail2963Hex), built);
        Assert.Equal(SkillDamageErrorPackets.SkillDamageErrorBodyLen, built.Length);
    }

    [Fact]
    public void PairedError2717ReproducesTheSameExchange()
    {
        var built = CombatSkillNotifyPackets.BuildSkillError2717(
            SkillDamageErrorPackets.ResultPrimaryTargetDead, 630310);

        Assert.Equal(Convert.FromHexString(Retail2717Hex), built);
    }
}
