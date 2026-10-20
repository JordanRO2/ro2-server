using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gate for the skill-damage family: RMI 2718 NfySkillDamage, 2797 NfySkillDamageTick,
/// 2495 OnDotDamageNotify and 2709 NfySkillCastingStart.
/// </summary>
/// <remarks>
/// Five of the six hex payloads are reference bodies in the enc-driven framing. The sixth, <c>Builder2797TwoTargetsHex</c>,
/// is builder-derived and is not a reference body. It is kept as the only multi-element case in this file.
/// Every enveloped body must start with a compact-length marker from {1, 2, 4, 8}, and the u16 at blob offset 0 must
/// equal the blob length. A malformed body crashes the client, so both halves are asserted for every builder.
/// </remarks>
public class SkilldamagePacketsTests
{
    
    
    
 /// <summary>
 /// Reference 2718, 82-byte body: skill 620410 from a PC caster on a PC primary target, zero position triple, castType 0,
 /// skillEffectIndex 1, one target element with HP 70,353 and SP 11,975, and one damage record (kind 1, damage 16,417,
 /// extra 0).
 /// </summary>
    private const string Retail2718Hex =
        "015050004be4000000007a770900114def0601115def060000000000000000000000" +
        "0000000000000000000001000001115def06d1120100c72e00000000000000000000" +
        "0000000001012140000000000000";

 /// <summary>Reference 2718 with an NPC target: skill 111001 from a PC caster, element HP 37 and SP 42, one record (kind 1, damage 13, extra 0).</summary>
    private const string Retail2718NpcTargetHex =
        "015050004be40000000099b1010011990607011376b5010000000000000000000000" +
        "00000000000000000000010000011376b501250000002a0000000000000000000000" +
        "0000000001010d00000000000000";

 /// <summary>Reference 2797, 46-byte body with one target: skill 620410, tick 1, PC caster, target with HP 53,936 and SP 11,975, and one embedded record (kind 1, damage 16,417).</summary>
    private const string Retail2797Hex =
        "012c2c006fe4000000007a77090001114def060100115def06b0d20000c72e000000" +
        "000000012140000000000000";

 /// <summary>
 /// Builder-derived two-target 2797, 71-byte body (69-byte blob): skill 2110101, tick 1, NPC caster and two PC targets,
 /// each with one record (kind 6, damage 0). It exercises the u16 count and the multi-element stride. It checks the
 /// builder against the length model and is not a reference body.
 /// </summary>
    private const string Builder2797TwoTargetsHex =
        "014545006fe400000000953220000113efec080200115def06e8480000ec2e000000" +
        "000000060000000000000000112cef06493f0000180f000000000000060000000000" +
        "000000";

 /// <summary>Reference 2495, 32-byte body: PC victim, NPC source, skill 2110101, negative damage (-17190) as in a damage-over-time tick, victim HP 16201 and SP 3864.</summary>
    private const string Retail2495Hex =
        "011e1e00e0e300000000112cef0613efec0895322000dabcffff493f0000180f";

 /// <summary>Reference 2709, 65-byte body: PC caster, skill 610410, 64-bit cast id, cast time about 0.8442 s, target, position triple, an all-zero triple at blob offset 45, and variant 257.</summary>
    private const string Retail2709Hex =
        "013f3f0048e400000000114def066a5009008a52c986e27bf3025d1f583f01115def" +
        "06d74d32441b9a38443a8c6242000000000000000000000000000000000101";

    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

 /// <summary>
 /// Safety property: an enveloped body starts with a legal compact-length marker, and the u16 at blob offset 0 equals the
 /// blob length. A body that violates either one crashes the client.
 /// </summary>
    private static void AssertEnvelopeIsWellFormed(byte[] body)
    {
        Assert.True(body[0] is 1 or 2 or 4 or 8,
            $"first body byte {body[0]} is not a compact-length marker in {{1,2,4,8}}");

        int markerWidth = body[0];
        long declared = 0;
        for (int i = 0; i < markerWidth; i++)
            declared |= (long)body[1 + i] << (8 * i);

        var blob = body.AsSpan(1 + markerWidth);
        Assert.Equal(blob.Length, (int)declared);
        Assert.Equal(blob.Length, (int)BinaryPrimitives.ReadUInt16LittleEndian(blob));

        
        
        Assert.Equal(blob.Length < 0x80 ? 1 : 2, markerWidth);
    }

    private static ReadOnlySpan<byte> BlobOf(byte[] body) => body.AsSpan(1 + body[0]);

    
    
    
    [Fact]
    public void SkillDamage2718ReproducesARealRetailFrameByteForByte()
    {
        var expected = Hex(Retail2718Hex);
        var body = SkillDamagePackets.BuildSkillDamage2718(
            casterHandle: 0x06EF4D11, skillId: 620410, primaryTargetHandle: 0x06EF5D11,
            hits: new[]
            {
                new SkillDamageHit(0x06EF5D11, 70353, 11975,
                    new[] { new SkillHitEffect(1, 16417) }),
            });
        Assert.Equal(expected, body);
    }

    [Fact]
    public void SkillDamage2718ReproducesARealRetailFrameWithAnNpcTarget()
    {
        var expected = Hex(Retail2718NpcTargetHex);
        var body = SkillDamagePackets.BuildSkillDamage2718(
            casterHandle: 0x07069911, skillId: 111001, primaryTargetHandle: 0x01B57613,
            hits: new[]
            {
                new SkillDamageHit(0x01B57613, 37, 42, new[] { new SkillHitEffect(1, 13) }),
            });
        Assert.Equal(expected, body);

        
        
        var blob = BlobOf(body);
        Assert.Equal(0x13u, BinaryPrimitives.ReadUInt32LittleEndian(blob[17..]) & 0xFFu);
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(blob[12..]) & 0xFFu);
    }

    [Fact]
    public void SkillDamage2718TakesTheCompactEnvelopeAndDeclaresItsOwnLength()
    {
        var body = SkillDamagePackets.BuildSkillDamage2718(
            0x06EF4D11, 620410, 0x06EF5D11,
            new[] { new SkillDamageHit(0x06EF5D11, 70353, 11975, new[] { new SkillHitEffect(1, 16417) }) });

        AssertEnvelopeIsWellFormed(body);

        var blob = BlobOf(body);
        
        Assert.Equal(SkillDamagePackets.MsgTagSkillDamage, BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]));
        
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]));
    }

 /// <summary>
 /// Nested length model: len(blob) == 46 + sum(25 + 9 * effectCount) over the targets. The builder must produce bodies
 /// that the client's walk consumes exactly; otherwise the client reads past the end.
 /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(20, 10)]   
    public void SkillDamage2718LengthModelHoldsForEveryShapeTheBuilderCanEmit(int targets, int effectsEach)
    {
        var fx = new SkillHitEffect[effectsEach];
        for (int i = 0; i < effectsEach; i++)
            fx[i] = new SkillHitEffect((byte)(i % 7), 100 + i);

        var hits = new SkillDamageHit[targets];
        for (int i = 0; i < targets; i++)
            hits[i] = new SkillDamageHit((uint)(0x06EF0011 + (i << 8)), 1000u + (uint)i, 50u + (uint)i, fx);

        var body = SkillDamagePackets.BuildSkillDamage2718(0x06EF4D11, 620410, 0x06EF5D11, hits);
        AssertEnvelopeIsWellFormed(body);

        var blob = BlobOf(body);
        Assert.Equal(SkillDamagePackets.SkillDamageHeadLen + targets * (SkillDamagePackets.SkillDamageElemLen
                        + effectsEach * SkillDamagePackets.HitEffectLen),
                     blob.Length);

        
        int count = blob[45];
        Assert.Equal(targets, count);
        int p = SkillDamagePackets.SkillDamageHeadLen;
        for (int i = 0; i < count; i++)
        {
            int inner = blob[p + 24];
            Assert.Equal(effectsEach, inner);
            p += SkillDamagePackets.SkillDamageElemLen + inner * SkillDamagePackets.HitEffectLen;
        }
        Assert.Equal(blob.Length, p);
    }

 /// <summary>The target-present flag at blob offset 16 is derived from the primary target and never taken from input: it is set exactly when the primary target is non-zero.</summary>
    [Fact]
    public void SkillDamage2718DerivesItsTargetPresenceByte()
    {
        var withTarget = BlobOf(SkillDamagePackets.BuildSkillDamage2718(
            0x06EF4D11, 620410, 0x06EF5D11, Array.Empty<SkillDamageHit>()));
        Assert.Equal((byte)1, withTarget[16]);

        var without = BlobOf(SkillDamagePackets.BuildSkillDamage2718(
            0x06EF4D11, 620410, 0, Array.Empty<SkillDamageHit>()));
        Assert.Equal((byte)0, without[16]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(without[17..]));
    }

 /// <summary>A wide broadcast pushes the blob past 127 bytes, and the marker must widen to two bytes. The client reads the width from the marker byte, so a wrong width shifts every field.</summary>
    [Fact]
    public void SkillDamage2718WidensTheCompactMarkerPastTheOneByteLimit()
    {
        var hits = new SkillDamageHit[8];
        for (int i = 0; i < hits.Length; i++)
            hits[i] = new SkillDamageHit((uint)(0x06EF0011 + (i << 8)), 500u, 10u, null);

        var body = SkillDamagePackets.BuildSkillDamage2718(0x06EF4D11, 620410, 0x06EF5D11, hits);
        Assert.Equal((byte)2, body[0]);                                   
        AssertEnvelopeIsWellFormed(body);
    }

 /// <summary>
 /// Crash gate: the client parses 2718 into a fixed-size stack buffer with a fixed element stride and no clamp on the wire
 /// counts. Outer targets are capped at 20 and effect records at 10, the maxima this server allows. Both refusals
 /// are asserted, since an oversized area-of-effect hit would overflow the client's buffer.
 /// </summary>
    [Fact]
    public void SkillDamage2718RefusesCountsThatWouldOverflowTheClientStackBuffer()
    {
        var tooManyTargets = new SkillDamageHit[SkillDamagePackets.MaxSkillDamageTargets + 1];
        for (int i = 0; i < tooManyTargets.Length; i++)
            tooManyTargets[i] = new SkillDamageHit((uint)(0x06EF0011 + (i << 8)), 1u, 1u, null);

        Assert.Throws<ArgumentException>(() => SkillDamagePackets.BuildSkillDamage2718(
            0x06EF4D11, 620410, 0x06EF5D11, tooManyTargets));

        var tooManyEffects = new SkillHitEffect[SkillDamagePackets.MaxSkillDamageEffectsPerTarget + 1];
        for (int i = 0; i < tooManyEffects.Length; i++)
            tooManyEffects[i] = new SkillHitEffect(1, i);

        Assert.Throws<ArgumentException>(() => SkillDamagePackets.BuildSkillDamage2718(
            0x06EF4D11, 620410, 0x06EF5D11,
            new[] { new SkillDamageHit(0x06EF5D11, 1u, 1u, tooManyEffects) }));

        
        
        var atCapacity = new SkillDamageHit[SkillDamagePackets.MaxSkillDamageTargets];
        for (int i = 0; i < atCapacity.Length; i++)
            atCapacity[i] = new SkillDamageHit((uint)(0x06EF0011 + (i << 8)), 1u, 1u, null);
        AssertEnvelopeIsWellFormed(SkillDamagePackets.BuildSkillDamage2718(
            0x06EF4D11, 620410, 0x06EF5D11, atCapacity));
    }

    
    
    
    [Fact]
    public void SkillDamageTick2797ReproducesARealRetailFrameByteForByte()
    {
        var expected = Hex(Retail2797Hex);
        var body = SkillDamagePackets.BuildSkillDamageTick2797(
            casterHandle: 0x06EF4D11, skillId: 620410, tickSequence: 1,
            hits: new[] { new SkillTickHit(0x06EF5D11, 53936, 11975, new SkillHitEffect(1, 16417)) });
        Assert.Equal(expected, body);
        AssertEnvelopeIsWellFormed(body);
    }

 /// <summary>Guards the multi-element stride and the u16 count against regression. The vector is builder-derived, not a reference body.</summary>
    [Fact]
    public void SkillDamageTick2797MatchesItsBuilderDerivedTwoTargetVector()
    {
        var expected = Hex(Builder2797TwoTargetsHex);
        var body = SkillDamagePackets.BuildSkillDamageTick2797(
            casterHandle: 0x08ECEF13, skillId: 2110101, tickSequence: 1,
            hits: new[]
            {
                new SkillTickHit(0x06EF5D11, 18664, 12012, new SkillHitEffect(6, 0)),
                new SkillTickHit(0x06EF2C11, 16201, 3864, new SkillHitEffect(6, 0)),
            });
        Assert.Equal(expected, body);

        var blob = BlobOf(body);
        Assert.Equal(SkillDamagePackets.MsgTagSkillDamageTick, BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]));
        Assert.Equal(2, (int)BinaryPrimitives.ReadUInt16LittleEndian(blob[17..]));   
        Assert.Equal(0x13u, BinaryPrimitives.ReadUInt32LittleEndian(blob[13..]) & 0xFFu);  
    }

 /// <summary>
 /// The 2797 blob length is 19 + 25 * count for every valid length. The element is fixed at 25 bytes, so the length
 /// model is a straight multiple.
 /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]       
    public void SkillDamageTick2797LengthModelIsBasePlusFixedStride(int targets)
    {
        var hits = new SkillTickHit[targets];
        for (int i = 0; i < targets; i++)
            hits[i] = new SkillTickHit((uint)(0x06EF0011 + (i << 8)), 900u, 20u, new SkillHitEffect(1, 7));

        var body = SkillDamagePackets.BuildSkillDamageTick2797(0x06EF4D11, 620410, 1, hits);
        AssertEnvelopeIsWellFormed(body);

        var blob = BlobOf(body);
        Assert.Equal(SkillDamagePackets.SkillDamageTickHeadLen
                     + targets * SkillDamagePackets.SkillDamageTickElemLen, blob.Length);
        Assert.Equal(targets, (int)BinaryPrimitives.ReadUInt16LittleEndian(blob[17..]));
    }

 /// <summary>A valid tick carries at least one target, so an empty tick is refused by the builder rather than produced.</summary>
    [Fact]
    public void SkillDamageTick2797RefusesAnEmptyTargetList()
    {
        Assert.Throws<ArgumentException>(() =>
            SkillDamagePackets.BuildSkillDamageTick2797(0x06EF4D11, 620410, 1, Array.Empty<SkillTickHit>()));
    }

 /// <summary>The 2797 element vector is heap-allocated on the client, so the cap is an envelope limit rather than a memory-safety one. The cap is 20.</summary>
    [Fact]
    public void SkillDamageTick2797RefusesMoreTargetsThanRetailEverSends()
    {
        var hits = new SkillTickHit[SkillDamagePackets.MaxSkillDamageTickTargets + 1];
        for (int i = 0; i < hits.Length; i++)
            hits[i] = new SkillTickHit((uint)(0x06EF0011 + (i << 8)), 1u, 1u, new SkillHitEffect(1, 1));

        Assert.Throws<ArgumentException>(() =>
            SkillDamagePackets.BuildSkillDamageTick2797(0x06EF4D11, 620410, 1, hits));
    }

    
    
    
    [Fact]
    public void DotDamageNotify2495ReproducesARealRetailFrameByteForByte()
    {
        var expected = Hex(Retail2495Hex);
        var body = SkillDamagePackets.BuildDotDamageNotify2495(
            victimHandle: 0x06EF2C11, sourceHandle: 0x08ECEF13, skillId: 2110101,
            damage: -17190, victimCurHp: 16201, victimCurSp: 3864);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void DotDamageNotify2495IsEnvelopedAndExactlyThirtyBlobBytes()
    {
        var body = SkillDamagePackets.BuildDotDamageNotify2495(
            0x06EF2C11, 0x08ECEF13, 2110101, -17190, 16201, 3864);
        AssertEnvelopeIsWellFormed(body);

        var blob = BlobOf(body);
        
        Assert.Equal(SkillDamagePackets.DotDamageNotifyBlobLen, blob.Length);
        Assert.Equal(SkillDamagePackets.MsgTagDotDamageNotify, BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]));
        
        
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]) & 0xFFu);
        Assert.Equal(0x13u, BinaryPrimitives.ReadUInt32LittleEndian(blob[12..]) & 0xFFu);
    }

 /// <summary>
 /// The damage slot is signed, and the client branches on <c>damage &lt;= 0</c>. A builder that wrote it unsigned would turn
 /// negative values into large positive ones.
 /// </summary>
    [Fact]
    public void DotDamageNotify2495KeepsTheDamageSlotSigned()
    {
        var blob = BlobOf(SkillDamagePackets.BuildDotDamageNotify2495(
            0x06EF2C11, 0x08ECEF13, 2110101, -17190, 16201, 3864));
        Assert.Equal(-17190, BinaryPrimitives.ReadInt32LittleEndian(blob[20..]));
    }

 /// <summary>The client reads SP with a 16-bit load, and SP values fit in 16 bits. The builder clamps rather than truncating silently into the next field.</summary>
    [Fact]
    public void DotDamageNotify2495ClampsSpToTheWidthTheClientReads()
    {
        var blob = BlobOf(SkillDamagePackets.BuildDotDamageNotify2495(
            0x06EF2C11, 0x08ECEF13, 2110101, -1, 16201, 1_000_000));
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(blob[28..]));
        Assert.Equal(SkillDamagePackets.DotDamageNotifyBlobLen, blob.Length);
    }

    
    
    
    [Fact]
    public void SkillCastingStart2709ReproducesARealRetailFrameByteForByte()
    {
        var expected = Hex(Retail2709Hex);
        var body = SkillDamagePackets.BuildSkillCastingStart2709(
            casterHandle: 0x06EF4D11, skillId: 610410,
            castId: 212649820270711434UL,
            castSeconds: BitConverter.Int32BitsToSingle(unchecked((int)0x3F581F5D)),
            targetHandle: 0x06EF5D11,
            x: BitConverter.Int32BitsToSingle(unchecked((int)0x44324DD7)),
            y: BitConverter.Int32BitsToSingle(unchecked((int)0x44389A1B)),
            z: BitConverter.Int32BitsToSingle(unchecked((int)0x42628C3A)));
        Assert.Equal(expected, body);
    }

    [Fact]
    public void SkillCastingStart2709IsEnvelopedAndExactlySixtyThreeBlobBytes()
    {
        var body = SkillDamagePackets.BuildSkillCastingStart2709(
            0x06EF4D11, 610410, 212649820270711434UL, 0.84422857f, 0x06EF5D11, 713.216f, 738.408f, 56.637f);
        AssertEnvelopeIsWellFormed(body);

        var blob = BlobOf(body);
        
        Assert.Equal(SkillDamagePackets.SkillCastingStartBlobLen, blob.Length);
        Assert.Equal(SkillDamagePackets.MsgTagSkillCastingStart, BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob[57..]));   
        Assert.Equal(SkillDamagePackets.CastingVariantModal, BinaryPrimitives.ReadUInt16LittleEndian(blob[61..]));
    }

 /// <summary>The 64-bit cast id at blob offset 16 is echoed from the C2S 1506 request at offset 4. It must land at offset 16 exactly.</summary>
    [Fact]
    public void SkillCastingStart2709PlacesTheCastIdAtBlobPlusSixteen()
    {
        const ulong castId = 212649820270711434UL;
        var blob = BlobOf(SkillDamagePackets.BuildSkillCastingStart2709(
            0x06EF4D11, 610410, castId, 0.5f, 0x06EF5D11, 1f, 2f, 3f));
        Assert.Equal(castId, BinaryPrimitives.ReadUInt64LittleEndian(blob[16..]));
        Assert.NotEqual(castId, BinaryPrimitives.ReadUInt64LittleEndian(blob[15..]));
    }

 /// <summary>Blob offset 28 is derived: it is set exactly when the target handle is non-zero.</summary>
    [Fact]
    public void SkillCastingStart2709DerivesItsTargetPresenceByte()
    {
        var present = BlobOf(SkillDamagePackets.BuildSkillCastingStart2709(
            0x06EF4D11, 610410, 1, 0.5f, 0x06EF5D11, 1f, 2f, 3f));
        Assert.Equal((byte)1, present[28]);

        var without = BlobOf(SkillDamagePackets.BuildSkillCastingStart2709(
            0x06EF4D11, 610410, 1, 0.5f, 0, 1f, 2f, 3f));
        Assert.Equal((byte)0, without[28]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(without[29..]));
    }
}
