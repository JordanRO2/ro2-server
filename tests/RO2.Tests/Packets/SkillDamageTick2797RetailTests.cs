using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Skill damage tick (RMI 2797 NfySkillDamageTick) against multi-target reference payloads, and the Skill_Type gate
/// that decides which C2S 1549 ticks receive a 2797.
/// </summary>
/// <remarks>
/// <para>
/// The payloads are S2C 2797 bodies in the enc-driven framing: UNENC uses a 3-byte header, RC4 and AES a 5-byte
/// header, and the opcode is read from the bytes. The five-target body has a blob longer than 128 bytes, so it also pins
/// the widening of the compact marker from one byte to two.
/// </para>
/// </remarks>
public class SkillDamageTick2797RetailTests
{
 /// <summary>
 /// Skill 510605 (a heal) tick 1 from a PC caster on two PC targets. Each target carries one record, and a negative
 /// amount is a heal.
 /// </summary>
    private const string Retail2797TwoTargetsHex =
        "014545006fe4000000008dca07000111e8f706020011e8f706c9d10000523f00000000000001c123ffff00000000" +
        "110ef70627dd0000be3500000000000001f829ffff00000000";

 /// <summary>
 /// Skill 210706 tick 1 from a PC caster on five NPC targets, each with one record. The blob is 144 bytes, so the
 /// compact prefix is 02 90 00.
 /// </summary>
    private const string Retail2797FiveTargetsHex =
        "02900090006fe400000000123703000111ad2b07050013540f00bdbb0100c333000000000000013b160000000000" +
        "0013520f0041b70100c33300000000000001b31a00000000000013530f00a0b70100c3330000000000000154" +
        "1a00000000000013570f0031ba0100023400000000000001661a00000000000013580f00edb90100023400000000" +
        "000001aa1a000000000000";

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void TwoTargetRetailFrameIsReproducedByteForByte()
    {
        var body = SkillDamagePackets.BuildSkillDamageTick2797(
            casterHandle: 0x06F7E811, skillId: 510605, tickSequence: 1,
            hits: new[]
            {
                new SkillTickHit(0x06F7E811, 53705, 16210, new SkillHitEffect(1, -56383)),
                new SkillTickHit(0x06F70E11, 56615, 13758, new SkillHitEffect(1, -54792)),
            });

        Assert.Equal(Hex(Retail2797TwoTargetsHex), body);
    }

    [Fact]
    public void FiveTargetRetailFrameIsReproducedByteForByteWithTheWideCompactMarker()
    {
        var body = SkillDamagePackets.BuildSkillDamageTick2797(
            casterHandle: 0x072BAD11, skillId: 210706, tickSequence: 1,
            hits: new[]
            {
                new SkillTickHit(0x000F5413, 113597, 13251, new SkillHitEffect(1, 5691)),
                new SkillTickHit(0x000F5213, 112449, 13251, new SkillHitEffect(1, 6835)),
                new SkillTickHit(0x000F5313, 112544, 13251, new SkillHitEffect(1, 6740)),
                new SkillTickHit(0x000F5713, 113201, 13314, new SkillHitEffect(1, 6758)),
                new SkillTickHit(0x000F5813, 113133, 13314, new SkillHitEffect(1, 6826)),
            });

        Assert.Equal(Hex(Retail2797FiveTargetsHex), body);
        Assert.Equal(2, body[0]);
        Assert.Equal(SkillDamagePackets.SkillDamageTickHeadLen + 5 * SkillDamagePackets.SkillDamageTickElemLen,
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1)));
    }

 /// <summary>
 /// The gate reads Skill_Type from the shipped skill table. The skills named here cover the attack skills that
 /// receive damage ticks and the buff and heal skills that do not.
 /// </summary>
    [Theory]
    [InlineData(521010u, true)]   
    [InlineData(631910u, false)]  
    [InlineData(510610u, false)]  
    [InlineData(2110101u, false)] 
    public void OnlyAttackSkillsAreDamageTickSkills(uint skillId, bool damages)
    {
        var types = SkillDamagePackets.ParseSkillTypes(
            File.ReadAllBytes(Path.Combine(RequireDataDirectory(), "Skill_Table_Base.json")));

        Assert.True(types.TryGetValue(skillId / 100, out var type), $"group {skillId / 100} missing");
        Assert.Equal(damages, type == SkillDamagePackets.AttackSkillType);
    }

    [Fact]
    public void ParseSkillTypesKeysEveryShippedGroup()
    {
        var types = SkillDamagePackets.ParseSkillTypes(
            File.ReadAllBytes(Path.Combine(RequireDataDirectory(), "Skill_Table_Base.json")));

        
        Assert.Equal(4328, types.Count);
        Assert.Equal(2135, types.Values.Count(t => t == SkillDamagePackets.AttackSkillType));
    }

 /// <summary>Walks up from the test output path to the repository's data directory.</summary>
    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Skill_Table_Base.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "no ancestor of " + AppContext.BaseDirectory + " holds data/Skill_Table_Base.json");
    }
}
