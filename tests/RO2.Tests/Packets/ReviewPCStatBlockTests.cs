using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>The 112-byte stat block that ends a full 3033 AnsReviewPC (<see cref="ReviewPCHandler.BuildStatBlock"/>).</summary>
/// <remarks>Bytes +6 through +35 of the block repeat the target's 2310 FinalTail. The tests check against this server's own 2310 bytes.</remarks>
public class ReviewPCStatBlockTests
{
 /// <summary>A 140-byte 2498 blob with only max HP / max SP set.</summary>
    private static byte[] Blob(uint maxHp, ushort maxSp)
    {
        var blob = new byte[TraitPackets.PcTraitBlobLen];
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(TraitPackets.PcTraitMaxHpOffset), maxHp);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(TraitPackets.PcTraitMaxSpOffset), maxSp);
        return blob;
    }

    [Fact]
    public void StatBlock_HeadIsZeroExceptTheTwoModelBytes()
    {
        var stats = ReviewPCHandler.BuildStatBlock(Blob(5000, 500), 4000, 300);

        Assert.Equal(112, stats.Length);
        for (int i = 0; i < 36; i++)
        {
            byte want = i is 10 or 11 ? ActorStatePackets.DetailTailModelClass : (byte)0;
            Assert.True(stats[i] == want, $"stat block +{i} is 0x{stats[i]:X2}, expected 0x{want:X2}");
        }
        
        Assert.Equal(0x10, stats[10]);
    }

    [Fact]
    public void StatBlock_FinalTailIsTheSameThirtyBytesOurOwn2310Ends()
    {
        var character = new Character { Id = 915_301, Name = "Reviewed", Job = 9, Level = 40 };
        using var w = new PacketWriter();
        WorldEntryService.WriteCharacterDetail(w, character, 0x0DE0B6B3A7F07304UL, 0x0137B811u, null, 1100u, 10f, 20f, 30f);
        var detail = w.ToArray();

        var stats = ReviewPCHandler.BuildStatBlock(Blob(5000, 500), 4000, 300);

        Assert.Equal(
            Convert.ToHexString(detail.AsSpan(detail.Length - ReviewPCHandler.FinalTailLength)),
            Convert.ToHexString(stats.AsSpan(ReviewPCHandler.StatBlockFinalTailOffset, ReviewPCHandler.FinalTailLength)));
    }

    [Fact]
    public void StatBlock_CarriesMaxAndCurrentHpSpClampedToTheSheet()
    {
        var stats = ReviewPCHandler.BuildStatBlock(Blob(5000, 500), 9000, -3);

        Assert.Equal(5000u, BinaryPrimitives.ReadUInt32LittleEndian(stats.AsSpan(48))); 
        Assert.Equal((ushort)500, BinaryPrimitives.ReadUInt16LittleEndian(stats.AsSpan(56))); 
        Assert.Equal(5000u, BinaryPrimitives.ReadUInt32LittleEndian(stats.AsSpan(52))); 
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(stats.AsSpan(58))); 
    }
}
