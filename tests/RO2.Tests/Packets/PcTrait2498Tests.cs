using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Gate for the vitals stamped into RMI 2498 OnPcTrait, the 143-byte trait blob sent for each character. The builder must
/// write max HP at blob+8 (body 11), max SP at blob+12 (body 15), current HP at blob+72 (body 75) and current SP at
/// blob+76 (body 79). An earlier version wrote max HP into the current-HP slot, so the current value equalled the maximum
/// and the donor's maximum was kept. These tests guard the slot assignment. See TraitPackets.BuildPcTrait2498.
/// </summary>
public class PcTrait2498Tests
{
 /// <summary>Vitals of the donor blob (level-1 values) at their correct offsets.</summary>
    private const uint DonorMaxHp = 224;
    private const ushort DonorMaxSp = 15;

 /// <summary>Body offsets, restated here so a silent shift in the builder fails this file.</summary>
    private const int MaxHpBodyOffset = 11;   
    private const int MaxSpBodyOffset = 15;   
    private const int CurHpBodyOffset = 75;   
    private const int CurSpBodyOffset = 79;   

 /// <summary>
 /// A 143-byte donor body with the width-2 compact prefix, the msgTag, and level-1 vitals in the four vital slots.
 /// </summary>
    private static byte[] DonorBody()
    {
        var b = new byte[TraitPackets.PcTraitBodyLen];
        b[0] = 0x02;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), (ushort)TraitPackets.PcTraitBlobLen);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), (ushort)TraitPackets.PcTraitBlobLen);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), TraitPackets.MsgTagPcTrait);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(MaxHpBodyOffset), DonorMaxHp);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(MaxSpBodyOffset), DonorMaxSp);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(CurHpBodyOffset), DonorMaxHp);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(CurSpBodyOffset), DonorMaxSp);
        
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(71), 1.0f);
        return b;
    }

    [Fact]
    public void TheFourVitalsLandInTheSlotsTheClientsSettersRead()
    {
        var body = WorldEntryService.BuildPcTrait2498FromDonor(
            DonorBody(), new Character { Hp = 9000, MaxHp = 12500, Mp = 1200, MaxMp = 3400 });

        Assert.Equal(TraitPackets.PcTraitBodyLen, body.Length);
        Assert.Equal(12500u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(MaxHpBodyOffset)));
        Assert.Equal(3400, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(MaxSpBodyOffset)));
        Assert.Equal(9000u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(CurHpBodyOffset)));
        Assert.Equal(1200, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(CurSpBodyOffset)));

        
        Assert.NotEqual(DonorMaxHp, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(MaxHpBodyOffset)));
        Assert.NotEqual(DonorMaxHp, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(CurHpBodyOffset)));
    }

 /// <summary>
 /// Regression: the old code wrote max HP at body 75, so the current slot held the maximum and the max slot held the
 /// donor's value. Current HP can never exceed max HP, so this inverted state never occurs.
 /// </summary>
    [Fact]
    public void CurrentVitalsNeverExceedTheMaxima()
    {
        var body = WorldEntryService.BuildPcTrait2498FromDonor(
            DonorBody(), new Character { Hp = 9000, MaxHp = 12500, Mp = 1200, MaxMp = 3400 });

        var maxHp = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(MaxHpBodyOffset));
        var curHp = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(CurHpBodyOffset));
        var maxSp = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(MaxSpBodyOffset));
        var curSp = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(CurSpBodyOffset));

        Assert.True(curHp <= maxHp);
        Assert.True(curSp <= maxSp);
    }

 /// <summary>
 /// The builder enforces current HP at most max HP rather than relying on argument order, since the client stores both
 /// values without checking them.
 /// </summary>
    [Fact]
    public void BuilderRefusesCurrentAboveMaximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TraitPackets.BuildPcTrait2498(maxHp: 100, maxSp: 50, curHp: 101, curSp: 10));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TraitPackets.BuildPcTrait2498(maxHp: 100, maxSp: 50, curHp: 10, curSp: 51));
    }

    [Fact]
    public void EverythingOutsideTheStampedSlotsKeepsTheDonorsBytes()
    {
        
        
        
        
        
        var donor = DonorBody();
        for (var i = 3; i < donor.Length; i++)
            donor[i] = (byte)(i + 1);                    
        
        BinaryPrimitives.WriteUInt32LittleEndian(donor.AsSpan(MaxHpBodyOffset), DonorMaxHp);
        BinaryPrimitives.WriteUInt16LittleEndian(donor.AsSpan(MaxSpBodyOffset), DonorMaxSp);
        BinaryPrimitives.WriteUInt32LittleEndian(donor.AsSpan(CurHpBodyOffset), DonorMaxHp);
        BinaryPrimitives.WriteUInt16LittleEndian(donor.AsSpan(CurSpBodyOffset), DonorMaxSp);
        
        BinaryPrimitives.WriteSingleLittleEndian(donor.AsSpan(71), 1.0f);

        var after = WorldEntryService.BuildPcTrait2498FromDonor(
            donor, new Character { Hp = 700, MaxHp = 9999, Mp = 100, MaxMp = 777 });

        for (var i = 0; i < donor.Length; i++)
        {
            var stamped =
                (i >= MaxHpBodyOffset && i < MaxHpBodyOffset + 4)
                || (i >= MaxSpBodyOffset && i < MaxSpBodyOffset + 2)
                || (i >= CurHpBodyOffset && i < CurHpBodyOffset + 4)
                || (i >= CurSpBodyOffset && i < CurSpBodyOffset + 2)
                || (i >= 17 && i < 29)                    
                || (i >= 29 && i < 39)                    
                || (i >= 41 && i < 43)                    
                || i < MaxHpBodyOffset;                   
            if (!stamped)
                Assert.Equal(donor[i], after[i]);
        }
    }

    [Fact]
    public void MaxSpIsClampedToItsSixteenBitSlot()
    {
        
        
        var body = WorldEntryService.BuildPcTrait2498FromDonor(
            DonorBody(), new Character { Hp = 1, MaxHp = 1, Mp = 1, MaxMp = 70000 });
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(MaxSpBodyOffset)));
    }

 /// <summary>
 /// A character entering the world with a stored HP of 0 is treated as fresh or unsaved rather than dead, and is sent at
 /// full vitals. This is a server policy, not a protocol rule.
 /// </summary>
    [Fact]
    public void AZeroStoredHpEntersTheWorldAtFull()
    {
        var body = WorldEntryService.BuildPcTrait2498FromDonor(
            DonorBody(), new Character { Hp = 0, MaxHp = 5000, Mp = 0, MaxMp = 900 });
        Assert.Equal(5000u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(CurHpBodyOffset)));
        Assert.Equal(900, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(CurSpBodyOffset)));
    }

 /// <summary>
 /// The client copies 140 bytes (0x8C) from the blob with no length check, so the blob length is a hard requirement.
 /// A short template is refused rather than padded.
 /// </summary>
    [Fact]
    public void ShortTemplateIsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => TraitPackets.BuildPcTrait2498(100, 50, 10, 10, 3.0f, new byte[139]));
    }

    [Fact]
    public void EnvelopeIsTheWidthTwoMarkerRetailUses()
    {
        var body = WorldEntryService.BuildPcTrait2498FromDonor(
            DonorBody(), new Character { Hp = 10, MaxHp = 20, Mp = 5, MaxMp = 10 });

        
        
        Assert.Equal(0x02, body[0]);
        Assert.Equal(TraitPackets.PcTraitBlobLen, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1)));
        Assert.Equal(TraitPackets.PcTraitBlobLen, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3)));
        Assert.Equal(TraitPackets.MsgTagPcTrait, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(5)));
        Assert.Equal(TraitPackets.PcTraitBlobLen, body.Length - 3);
    }
}
