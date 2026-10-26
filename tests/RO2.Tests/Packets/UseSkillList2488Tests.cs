using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gate for RMI 2488 OnUseSkillList, the learned-skill list sent at world entry.
/// </summary>
/// <remarks>
/// The body length depends on the skill count: a 14-byte header followed by one u32 skill id per entry.
/// Reference bodies with counts 3 and 4:
/// count 3 (len 26): 01 18 18 00 b3 e3 00 00 00 00 03 00 00 00 &lt;3 x u32 skillId&gt;
/// count 4 (len 30): 01 1c 1c 00 b3 e3 00 00 00 00 04 00 00 00 &lt;4 x u32 skillId&gt;
/// Every skill id in the fixtures ends in 01 (level 1). The builder takes (Skill_Group, Level) and emits
/// group*100+level, so the tests write their ids as groups at level 1.
/// The tests guard two defects that shipped earlier:
/// 1. The length was once hardcoded to the count-3 size. With an empty list the client read past the message
/// into uninitialised memory and crashed shortly after world entry.
/// 2. A u32 entity id was written over the msgTag and the zero bytes that follow it. The count still landed at
/// the right offset, so parsing continued with the wrong tag; only an explicit byte assertion catches this.
/// </remarks>
public class UseSkillList2488Tests
{
    [Fact]
    public void ThreeSkills_ReproducesTheRetailPrefixByteForByte()
    {
        
        var body = SkillPackets.BuildUseSkillList2488(
            new (uint, byte)[] { (4101u, 1), (4103u, 1), (4108u, 1) });
        Assert.Equal(Convert.FromHexString("011818" + "00b3e3" + "00000000" + "03000000"),
            body.AsSpan(0, 14).ToArray());
        Assert.Equal(26, body.Length);
    }

    [Fact]
    public void FourSkills_ReproducesTheOtherObservedRetailLength()
    {
        
        
        var body = SkillPackets.BuildUseSkillList2488(
            new (uint, byte)[] { (4101u, 1), (4103u, 1), (4108u, 1), (6101u, 1) });
        Assert.Equal(Convert.FromHexString("011c1c" + "00b3e3" + "00000000" + "04000000"),
            body.AsSpan(0, 14).ToArray());
        Assert.Equal(30, body.Length);
    }

    [Fact]
    public void MsgTag_IsAtBlobPlus2_AndIsNotAnEntityId()
    {
        var body = SkillPackets.BuildUseSkillList2488(new (uint, byte)[] { (4101u, 1) });
        
        Assert.Equal(0xE3B3, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        Assert.Equal(SkillPackets.MsgTagUseSkillList,
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)));
    }

    [Fact]
    public void EmptyList_StillDeclaresItsRealLength()
    {
        
        var body = SkillPackets.BuildUseSkillList2488(Array.Empty<(uint, byte)>());
        Assert.Equal(14, body.Length);
        Assert.Equal(12, body[1]);
        Assert.Equal(12, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10)));  
    }

    [Theory]
    [InlineData(0, 14)]
    [InlineData(1, 18)]
    [InlineData(3, 26)]
    [InlineData(4, 30)]
    [InlineData(10, 54)]
    
    
    
    [InlineData(29, 131)]
    
    [InlineData(61, 259)]
    public void DeclaredLength_AlwaysEqualsTheRealContentLength(int count, int expectedTotal)
    {
        
        
        var skills = Enumerable.Range(1, count).Select(i => ((uint)(4100 + i), (byte)1)).ToArray();
        var body = SkillPackets.BuildUseSkillList2488(skills);
        Assert.Equal(expectedTotal, body.Length);

        
        
        
        int width = body[0];
        Assert.True(width is 1 or 2, $"unexpected compact-length width {width} for {count} skills");
        long declared = width == 1
            ? body[1]
            : BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1));
        Assert.Equal(body.Length - 1 - width, declared);
        Assert.Equal(declared, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1 + width)));
        Assert.Equal(count, (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1 + width + 8)));
    }

    [Fact]
    public void SkillIds_StartAtBlobPlus12()
    {
        
        
        var body = SkillPackets.BuildUseSkillList2488(
            new (uint, byte)[] { (4101u, 1), (4103u, 1) });
        Assert.Equal(410101u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(14)));
        Assert.Equal(410301u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(18)));
    }
}
