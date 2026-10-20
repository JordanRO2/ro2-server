using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout tests for the trait family: 2497 OnChangeActorMoveSpeed, 2498 OnPcTrait,
/// 2499 OnPcTraitChange and 2986 NfyJobMasteryPointChange.
/// </summary>
/// <remarks>
/// Each builder is checked byte for byte against a reference frame. Each opcode has a single body length,
/// so only a byte-exact comparison can confirm the field order.
/// 2497, 2498 and 2499 are ByteArray opcodes; 2986 uses the U32 envelope and is excluded from the
/// compact-length rule that applies to the other three.
/// </remarks>
public class TraitsPacketsTests
{
    
    private const string Retail2497 = "01101000fce30000000011d7ea060000a040";

    
    private const string Retail2497Npc = "01101000fce300000000139623010000c03f";

    
    private const string Retail2499 = "01191900fde30000000004000000400000000000fa43409c000001";

    
    private const string Retail2986 = "100000000000000000000000b4035f0200000000";

    
    private const string Retail2986Delta = "100000008076000000000000e135eb0000000000";

    
    
    
    
    private const string Retail2498 =
        "028c008c00cce30000000084c10000203720070b047e171003750d0000000000007701000000" +
        "0000000000be300000f1d900000000ec37000001000000820400000000000000000000404037" +
        "be00000430d000240000009e1b600580270c04500bed1ef60b9e0100000000000000000000000" +
        "0000000000000000000000000000000007d02fb075701d007f401b80b";

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    
    [Fact]
    public void Build2497_ReproducesARealRetailFrame_ByteForByte()
    {
        var expected = Hex(Retail2497);
        Assert.Equal(expected, TraitPackets.BuildActorMoveSpeed2497(0x06EAD711u, 5.0f));
    }

    [Fact]
    public void Build2497_ReproducesARealRetailFrameCarryingAnNpcHandle_ByteForByte()
    {
        
        var expected = Hex(Retail2497Npc);
        Assert.Equal(expected, TraitPackets.BuildActorMoveSpeed2497(0x01239613u, 1.5f));
    }

    
    [Fact]
    public void Build2498_ReproducesARealRetailFrame_ByteForByte()
    {
        var expected = Hex(Retail2498);
        Assert.Equal(TraitPackets.PcTraitBodyLen, expected.Length);

        
        
        var built = TraitPackets.BuildPcTrait2498(
            maxHp: 49540, maxSp: 14112, curHp: 48695, curSp: 12292,
            scaleAtBlob68: 3.0f,
            traitTemplate: expected.AsSpan(3));

        Assert.Equal(expected, built);
    }

    [Fact]
    public void Build2498_RefusesTheSwappedVitalPairsOutright()
    {
        
        
        
        
        
        
        
        var expected = Hex(Retail2498);

        Assert.Throws<ArgumentOutOfRangeException>(() => TraitPackets.BuildPcTrait2498(
            maxHp: 48695, maxSp: 12292, curHp: 49540, curSp: 14112,
            scaleAtBlob68: 3.0f,
            traitTemplate: expected.AsSpan(3)));
    }

    [Fact]
    public void Build2498_FirstBodyByteIsAValidCompactMarker_AndTheBlobIsExactly140Bytes()
    {
        var body = TraitPackets.BuildPcTrait2498(1000, 100, 900, 90);
        
        Assert.Equal(TraitPackets.PcTraitBodyLen, body.Length);
        Assert.Equal(0x02, body[0]);
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(TraitPackets.PcTraitBlobLen, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1)));
        
        Assert.Equal(TraitPackets.PcTraitBlobLen, body.Length - 3);
    }

    [Fact]
    public void Build2498_PutsSelfLenAndMsgTagWhereEveryRetailFrameHasThem()
    {
        var body = TraitPackets.BuildPcTrait2498(1000, 100, 900, 90);
        Assert.Equal(TraitPackets.PcTraitBlobLen, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3)));
        Assert.Equal(TraitPackets.MsgTagPcTrait, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(5)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(7)));
    }

    [Fact]
    public void Build2498_VitalsLandOnTheOffsetsTheClientReads()
    {
        
        
        
        
        var body = TraitPackets.BuildPcTrait2498(maxHp: 123456, maxSp: 7890, curHp: 654, curSp: 21);
        Assert.Equal(123456u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(11)));
        Assert.Equal(7890, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(15)));
        Assert.Equal(654u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(75)));
        Assert.Equal(21, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(79)));
        Assert.Equal(8, TraitPackets.PcTraitMaxHpOffset);
        Assert.Equal(12, TraitPackets.PcTraitMaxSpOffset);
        Assert.Equal(72, TraitPackets.PcTraitCurHpOffset);
        Assert.Equal(76, TraitPackets.PcTraitCurSpOffset);
    }

    [Fact]
    public void Build2498_DefaultsTheUndeterminedFloatToItsMeasuredModalValue()
    {
        
        var body = TraitPackets.BuildPcTrait2498(1000, 100, 900, 90);
        Assert.Equal(3.0f, BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(71)));
    }

    [Fact]
    public void Build2498_ZeroesTheRangeRetailAlwaysZeroes()
    {
        
        var body = TraitPackets.BuildPcTrait2498(1000, 100, 900, 90);
        for (var i = 107; i <= 130; i++)
            Assert.Equal(0, body[i]);
    }

    [Fact]
    public void Build2498_RejectsATemplateThatIsNotExactly140Bytes()
    {
        
        Assert.Throws<ArgumentException>(
            () => TraitPackets.BuildPcTrait2498(1, 1, 1, 1, 3.0f, new byte[139]));
        Assert.Throws<ArgumentException>(
            () => TraitPackets.BuildPcTrait2498(1, 1, 1, 1, 3.0f, new byte[141]));
    }

    
    [Fact]
    public void Build2499_ReproducesARealRetailFrame_ByteForByte()
    {
        var expected = Hex(Retail2499);
        Assert.Equal(TraitPackets.PcTraitChangeBodyLen, expected.Length);
        var built = TraitPackets.BuildPcTraitChange2499(
            traitKind: 4, traitId: 64, value: 500.0f, effectId: 40000);
        Assert.Equal(expected, built);
    }

    [Fact]
    public void Build2499_FirstBodyByteIsAValidCompactMarker()
    {
        var body = TraitPackets.BuildPcTraitChange2499(4, 64, 500.0f, 40000);
        AssertCompactEnvelope(body, prefix: 2, expectedTag: 0xE3FD);
    }

    [Fact]
    public void Build2499_KeepsTheClientsIdentityKeyAndPayloadWhereItLooksForThem()
    {
        
        
        
        var body = TraitPackets.BuildPcTraitChange2499(3, 21, -10000.0f, 105202, flag: 1);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10)));   
        Assert.Equal(21u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(14)));  
        Assert.Equal(-10000.0f, BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(18))); 
        Assert.Equal(105202u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(22))); 
        Assert.Equal(1, body[26]);                                                    
    }

    
    [Fact]
    public void Build2986_ReproducesARealRetailFrame_ByteForByte()
    {
        Assert.Equal(Hex(Retail2986), TraitPackets.BuildJobMasteryPointChange2986(0, 39781300));
    }

    [Fact]
    public void Build2986_ReproducesARealRetailFrameWithANonZeroDelta_ByteForByte()
    {
        Assert.Equal(Hex(Retail2986Delta),
            TraitPackets.BuildJobMasteryPointChange2986(30336, 15414753));
    }

    [Fact]
    public void Build2986_UsesTheU32Envelope_NotACompactMarker()
    {
        
        
        
        var body = TraitPackets.BuildJobMasteryPointChange2986(-5, 1234);
        Assert.Equal(TraitPackets.JobMasteryBodyLen, body.Length);
        Assert.Equal(TraitPackets.JobMasteryBlobLen, BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(0x10, body[0]);
        Assert.DoesNotContain(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal((uint)(body.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(body));
    }

    [Fact]
    public void Build2986_WritesTheDeltaAsOneSignExtendedInt64()
    {
        
        
        
        var body = TraitPackets.BuildJobMasteryPointChange2986(-90000000L, 42);
        Assert.Equal(-90000000L, BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(4)));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    
 /// <summary>
 /// The safety property every ByteArray opcode in this family must satisfy: the first body byte is one of the
 /// compact-length markers {1,2,4,8}, the encoded length equals the blob length, and the blob's length field
 /// repeats that length while the msgTag follows at offset 2.
 /// </summary>
    private static void AssertCompactEnvelope(byte[] body, int prefix, ushort expectedTag)
    {
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        var blobLen = body.Length - prefix;
        var encoded = prefix == 2
            ? body[1]
            : BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1));
        Assert.Equal(blobLen, encoded);
        Assert.Equal(blobLen, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(prefix)));
        Assert.Equal(expectedTag, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(prefix + 2)));
    }

    [Fact]
    public void EveryByteArrayBuilderInThisFamilySatisfiesTheFirstBodyByteRule()
    {
        AssertCompactEnvelope(TraitPackets.BuildActorMoveSpeed2497(0x0137B811u, 5.0f), 2, 0xE3FC);
        AssertCompactEnvelope(TraitPackets.BuildPcTraitChange2499(4, 25, 1000f, 40012), 2, 0xE3FD);

        var trait = TraitPackets.BuildPcTrait2498(50000, 12000, 49000, 11000);
        Assert.Contains(trait[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(trait.Length - 3, BinaryPrimitives.ReadUInt16LittleEndian(trait.AsSpan(1)));
        Assert.Equal(trait.Length - 3, BinaryPrimitives.ReadUInt16LittleEndian(trait.AsSpan(3)));
        Assert.Equal(TraitPackets.MsgTagPcTrait, BinaryPrimitives.ReadUInt16LittleEndian(trait.AsSpan(5)));
    }
}
