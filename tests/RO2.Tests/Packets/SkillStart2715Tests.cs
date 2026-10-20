using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gate for the skill-start pair: RMI 2715 AnsUseSkillStart and RMI 2716 NfyUseSkillStart.
/// </summary>
/// <remarks>
/// <para>
/// The payloads are reference bodies in the enc-driven framing, not hand-built vectors.
/// </para>
/// <para>
/// The two opcodes use different envelopes. 2715 is bare (its first byte is the low half of a u16 result code), and
/// 2716 carries the compact-length envelope. The compact-length decoder accepts only the marker bytes 1, 2, 4 and 8.
/// A rejected body leaves the consumer with a null blob, so the envelope must match the opcode.
/// </para>
/// </remarks>
public class SkillStart2715Tests
{
 /// <summary>
 /// Reference 2715: result code 0, skill 410801, target handle with the PC tag, cooldown 3.0 s, zero position triple,
 /// variant 1.
 /// </summary>
 /// <remarks>
 /// The cooldown matches Skill_Table_SkillLevel.Cooldown_Time for this skill, so the value the handler takes from game
 /// data equals the reference value on this payload.
 /// </remarks>
    private const string Retail2715Hex =
        "0000b14406001111210701000040400000000000000000000000000100";

 /// <summary>
 /// Reference 2716, 67 bytes: skill 114401, PC caster with current HP 63,235 and SP 19,493, an NPC target, variant 1,
 /// and no extra targets.
 /// </summary>
    private const string Retail2716Hex =
        "014141004ae400000000e1be010011570f0703f70000254c00000000000000000000" +
        "000000000000000000000000000000000113e89d01010000000000000000000000";

    [Fact]
    public void Ack2715ReproducesARealRetailFrameByteForByte()
    {
        var expected = Convert.FromHexString(Retail2715Hex);
        var body = SkillPackets.BuildUseSkillStartAck2715(
            skillId: 410801, targetHandle: 0x07211111, cooldownSeconds: 3.0f,
            targetX: 0f, targetY: 0f, targetZ: 0f);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void Ack2715IsBareAndMustNotTakeTheCompactEnvelope()
    {
        var body = SkillPackets.BuildUseSkillStartAck2715(410801, 0x07211111, 3.0f, 0f, 0f, 0f);

        
        
        Assert.Equal(SkillPackets.UseSkillStartAckBodyLen, body.Length);

        
        
        
        Assert.Equal(SkillPackets.SkillStartOk,
            BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.DoesNotContain(body[0], new byte[] { 1, 2, 4, 8 });
    }

    [Fact]
    public void Ack2715CarriesTheTargetHandleAtSixAndTheSkillIdAtTwo()
    {
        
        
        
        
        
        
        var body = SkillPackets.BuildUseSkillStartAck2715(
            skillId: 0x000644B1, targetHandle: 0x06ABCD13, cooldownSeconds: 1.5f,
            targetX: 0f, targetY: 0f, targetZ: 0f);

        Assert.Equal(0x000644B1u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2)));
        Assert.Equal(0x06ABCD13u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(11)));
        Assert.Equal(SkillPackets.SkillVariantModal,
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(27)));

        
        Assert.NotEqual(0x06ABCD13u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2)));
    }

    [Fact]
    public void Ack2715EchoesTheRequestPositionTriple()
    {
        
        
        
        var body = SkillPackets.BuildUseSkillStartAck2715(
            410801, 0x07211111, 3.0f, targetX: 671.85f, targetY: 284.7f, targetZ: 47.565f);

        Assert.Equal(671.85f, BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(15)));
        Assert.Equal(284.7f, BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(19)));
        Assert.Equal(47.565f, BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(23)));
    }

    [Fact]
    public void Ack2715TargetFlagMirrorsWhetherThereIsATarget()
    {
        
        
        
        
        var withTarget = SkillPackets.BuildUseSkillStartAck2715(410801, 0x06ABCD13, 3f, 0f, 0f, 0f);
        var noTarget = SkillPackets.BuildUseSkillStartAck2715(410801, 0, 3f, 0f, 0f, 0f);

        Assert.Equal(1, withTarget[10]);
        Assert.Equal(0, noTarget[10]);
    }

    [Fact]
    public void Notify2716ReproducesARealRetailFrameByteForByte()
    {
        var expected = Convert.FromHexString(Retail2716Hex);
        var body = SkillPackets.BuildUseSkillStart2716(
            casterHandle: 0x070F5711, skillId: 114401,
            casterCurHp: 63235, casterCurSp: 19493, targetHandle: 0x019DE813);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void Notify2716CarriesTheEnvelopeTheClientRequires()
    {
        var body = SkillPackets.BuildUseSkillStart2716(0x00000111, 410801, 100, 50, 0x06ABCD13);

        
        
        
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(SkillPackets.UseSkillStartNotifyBodyLen, body.Length);

        var blob = body.AsSpan(1 + body[0]);
        Assert.Equal(blob.Length, body[1]);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(SkillPackets.MsgTagUseSkillStart,
            BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Notify2716LengthFollowsTheSixtyFivePlusFourCountModel(int count)
    {
        var extra = Enumerable.Range(0, count).Select(i => 0x06000011u + (uint)i).ToArray();
        var body = SkillPackets.BuildUseSkillStart2716(
            0x00000111, 410801, 100, 50, 0x06ABCD13, extraTargets: extra);
        var blob = body.AsSpan(1 + body[0]);

        
        
        
        Assert.Equal(65 + 4 * count, blob.Length);
        Assert.Equal((ushort)count, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(55)));
        for (var i = 0; i < count; i++)
            Assert.Equal(extra[i], BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(57 + 4 * i)));

        
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(blob.Slice(57 + 4 * count)));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(blob.Slice(61 + 4 * count)));
    }

    [Fact]
    public void Notify2716TargetFlagMirrorsWhetherThereIsATarget()
    {
        
        
        
        var withTarget = SkillPackets.BuildUseSkillStart2716(0x00000111, 410801, 100, 50, 0x06ABCD13);
        var noTarget = SkillPackets.BuildUseSkillStart2716(0x00000111, 410801, 100, 50, 0);

        Assert.Equal(1, withTarget.AsSpan(1 + withTarget[0])[48]);
        Assert.Equal(0, noTarget.AsSpan(1 + noTarget[0])[48]);
        Assert.Equal(0x06ABCD13u,
            BinaryPrimitives.ReadUInt32LittleEndian(withTarget.AsSpan(1 + withTarget[0] + 49)));
    }

    [Fact]
    public void Notify2716ClampsSpToSixteenBits()
    {
        
        
        
        var body = SkillPackets.BuildUseSkillStart2716(0x00000111, 410801, 1_000_000, 70_000, 0);
        var blob = body.AsSpan(1 + body[0]);

        Assert.Equal(1_000_000u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16)));
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(20)));
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(20)) <= ushort.MaxValue);
    }
}
