using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout tests for the effects family: 2492 OnAddEffect, 2494 OnDelEffect, 2493 OnDelEffectSuccess
/// and 2819 NfyNpcEffect.
/// </summary>
/// <remarks>
/// <para>
/// The hex vectors below are reference frame bodies.
/// </para>
/// <para>
/// All four opcodes are enveloped. A malformed body is a client crash rather than a dropped packet, because
/// the compact-length decoder rejects it and the handler then dereferences a null buffer.
/// <see cref="AssertWellFormed"/> runs on every builder to check the first-body-byte rule and that selfLen
/// equals the blob length.
/// </para>
/// </remarks>
public class EffectsPacketsTests
{
    private static ReadOnlySpan<byte> Blob(byte[] body) => body.AsSpan(1 + body[0]);

 /// <summary>
 /// The safety property asserted for every builder: the first body byte is a compact-length marker in
 /// {1, 2, 4, 8}, and the u16 at blob offset 0 equals the blob length.
 /// </summary>
    private static void AssertWellFormed(byte[] body, int len, ushort tag)
    {
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(len, body.Length);
        var blob = Blob(body);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(tag, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
    }

    
    
    
    
    private const string AddEffectFrame =
        "01242400c7e300000000115def062e1201004c43742700000000000001953220000013efec08";

    [Fact]
    public void AddEffect2492_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString(AddEffectFrame);
        var body = EffectPackets.BuildAddEffect2492(
            targetHandle: 0x06EF5D11u, effectId: 70190, effectValue: 17228,
            durationMs: 10100, stackCount: 1, sourceSkillId: 2110101,
            casterHandle: 0x08ECEF13u, applyMode: 0);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void AddEffect2492_HandleComesBeforeEffectId()
    {
        
        
        
        
        var body = EffectPackets.BuildAddEffect2492(
            0x06EF5D11u, 70190, 17228, 10100, 1, 2110101, 0x08ECEF13u);
        var blob = Blob(body);
        Assert.Equal(0x06EF5D11u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8)));
        Assert.Equal(70190u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12)));
        Assert.Equal((byte)0x11, blob[8]);          
        Assert.Equal((byte)0x13, blob[32]);         
    }

    [Fact]
    public void AddEffect2492_TailFieldsAreMisalignedExactlyAsTheConsumerReadsThem()
    {
        
        
        
        var body = EffectPackets.BuildAddEffect2492(
            targetHandle: 0x0102_0311u, effectId: 505111, effectValue: 0xBEEF,
            durationMs: 0x0000_0001_2345_6789UL, stackCount: 100, sourceSkillId: 10800401,
            casterHandle: 0x0405_0613u, applyMode: 2);
        AssertWellFormed(body, EffectPackets.AddEffectBodyLen, EffectPackets.MsgTagAddEffect);
        var blob = Blob(body);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
        Assert.Equal((ushort)0xBEEF, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(16)));
        Assert.Equal(0x0000_0001_2345_6789UL, BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(18)));
        Assert.Equal((byte)100, blob[26]);
        Assert.Equal(10800401u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(27)));
        Assert.Equal((byte)2, blob[31]);
        Assert.Equal(0x0405_0613u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(32)));
    }

    [Fact]
    public void AddEffect2492_PermanentEffectsSendZeroDuration()
    {
        
        var body = EffectPackets.BuildAddEffect2492(
            0x06EF5D11u, 70190, 1, 0, 1, 0, 0x06EF5D11u);
        AssertWellFormed(body, EffectPackets.AddEffectBodyLen, EffectPackets.MsgTagAddEffect);
        var blob = Blob(body);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(18)));
        
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(27)));
    }

    
    
    
    
    
    
    
    private const string DelEffectFrame =
        "01181800c9e3000000002e120100136a9f090000000000000000";

    [Fact]
    public void DelEffect2494_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString(DelEffectFrame);
        var body = ActorVitalsPackets.BuildDelEffect2494(effectId: 70190, actorHandle: 0x099F6A13u);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void DelEffect2494_EffectIdComesBeforeHandle_TheReverseOf2492()
    {
        
        
        
        
        var body = ActorVitalsPackets.BuildDelEffect2494(70190, 0x099F6A13u);
        var blob = Blob(body);
        Assert.Equal(70190u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8)));
        Assert.Equal(0x099F6A13u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12)));
        Assert.Equal((byte)0x13, blob[12]);         
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(16)));
    }

    [Fact]
    public void DelEffect2494_DoesNotShareTheFieldOrderOf2492()
    {
        
        
        const uint handle = 0x06EF5D11u;
        const uint effect = 70190u;
        var add = Blob(EffectPackets.BuildAddEffect2492(handle, effect, 1, 0, 1, 0, handle));
        var del = Blob(ActorVitalsPackets.BuildDelEffect2494(effect, handle));
        Assert.Equal(handle, BinaryPrimitives.ReadUInt32LittleEndian(add.Slice(8)));
        Assert.Equal(effect, BinaryPrimitives.ReadUInt32LittleEndian(del.Slice(8)));
        Assert.Equal(effect, BinaryPrimitives.ReadUInt32LittleEndian(add.Slice(12)));
        Assert.Equal(handle, BinaryPrimitives.ReadUInt32LittleEndian(del.Slice(12)));
    }

    
    
    private const string DelEffectSuccessFrame =
        "01141400c8e3000000000b5a00000000000000000000";

    [Fact]
    public void DelEffectSuccess2493_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString(DelEffectSuccessFrame);
        var body = EffectPackets.BuildDelEffectSuccess2493(effectId: 23051);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void DelEffectSuccess2493_CarriesNoActorHandle()
    {
        
        
        
        
        var body = EffectPackets.BuildDelEffectSuccess2493(23051);
        var blob = Blob(body);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
        Assert.Equal(23051u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(12)));
        Assert.Equal(20, blob.Length);
    }

    
    
    private const string NpcEffectFrame = "010d0d0071e40000000013efec0801";

    [Fact]
    public void NpcEffect2819_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString(NpcEffectFrame);
        var body = EffectPackets.BuildNpcEffect2819(npcHandle: 0x08ECEF13u);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void NpcEffect2819_SubjectIsAnNpcAndTheTrailingByteIsTheMeasuredConstant()
    {
        
        
        
        var body = EffectPackets.BuildNpcEffect2819(0x08ECEF13u);
        var blob = Blob(body);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
        Assert.Equal(0x08ECEF13u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8)));
        Assert.Equal((byte)0x13, blob[8]);
        Assert.Equal((byte)1, blob[12]);
        Assert.Equal(13, blob.Length);
    }

    
    [Fact]
    public void EveryEffectsBuilderEmitsAValidCompactEnvelope()
    {
        
        AssertWellFormed(EffectPackets.BuildAddEffect2492(0x11u, 20, 0, 0, 1, 0, 0x13u),
                         EffectPackets.AddEffectBodyLen, EffectPackets.MsgTagAddEffect);
        AssertWellFormed(ActorVitalsPackets.BuildDelEffect2494(505111, 0x13u),
                         ActorVitalsPackets.DelEffectBodyLen, ActorVitalsPackets.MsgTagDelEffect);
        AssertWellFormed(EffectPackets.BuildDelEffectSuccess2493(505111),
                         EffectPackets.DelEffectSuccessBodyLen, EffectPackets.MsgTagDelEffectSuccess);
        AssertWellFormed(EffectPackets.BuildNpcEffect2819(0x13u),
                         EffectPackets.NpcEffectBodyLen, EffectPackets.MsgTagNpcEffect);
    }
}
