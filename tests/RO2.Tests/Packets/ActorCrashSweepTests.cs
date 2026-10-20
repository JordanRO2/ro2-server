using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Crash-sweep tests for the "actor" compact-BA family (see <see cref="ActorStatePackets"/>).
/// Each builder must emit a body that starts with a valid compact marker and has a self-consistent length prefix,
/// so the client never receives an empty ByteArray.
/// </summary>
/// <remarks>
/// Body form is [compactLen(selfLen)][u16 selfLen][u16 msgTag][fields], where selfLen is 2 plus the payload length.
/// </remarks>
public class ActorCrashSweepTests
{
    private static float F(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));

    
    [Fact]
    public void OnSitDown2322_ReproducesRetailFrame()
    {
        
        var expected = Convert.FromHexString("010b0b0000e311ee2c07000001");
        var body = ActorStatePackets.BuildOnSitDown(0x072CEE11u, 0x01);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void OnEmoticonAni2397_ReproducesRetailFrame()
    {
        
        var expected = Convert.FromHexString("01101000d8e311b0270711b0270703000000");
        var body = ActorStatePackets.BuildOnEmoticonAni(0x0727B011u, 3u);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void OnRevival2399_ReproducesRetailFrame()
    {
        
        
        var expected = Convert.FromHexString(
            "011b1b00e9e211811c0711811c07000001ae47fc42713ddb42a3afa140");
        var body = ActorStatePackets.BuildOnRevival(
            0x071C8111u, F(0x42FC47AEu), F(0x42DB3D71u), F(0x40A1AFA3u));
        Assert.Equal(expected, body);
    }

    [Fact]
    public void OnAckTransModeStart2530_ReproducesRetailFrame()
    {
        
        var expected = Convert.FromHexString("010a0a0025e4000000000000");
        var body = ActorStatePackets.BuildOnAckTransModeStart();
        Assert.Equal(expected, body);
    }

    [Fact]
    public void OnSkillChannelingCancelNotify2585_ReproducesRetailFrame()
    {
        
        
        
        var expected = Convert.FromHexString("01101000b0e30000000011f752088a9e0900");
        var body = CombatSkillNotifyPackets.BuildSkillChannelingCancelNotify2585(0x0852F711u, 0x00099E8Au);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void OnSkillChannelingCompleteNotify2586_ReproducesRetailFrame()
    {
        
        var expected = Convert.FromHexString("01101000b1e30000000011f79208819e0900");
        var body = ActorStatePackets.BuildOnSkillChannelingCompleteNotify(0x0892F711u, 0x00099E81u);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void NfyRideRidingPet2720_ReproducesRetailFrame()
    {
        
        
        var expected = Convert.FromHexString("011414004ce400000000ad8e5b001157ec06a0985d05");
        var body = ActorStatePackets.BuildNfyRideRidingPet(0x005B8EADu, 0x06EC5711u, 0x055D98A0u);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void OnMountFarGlider2731_ReproducesRetailFrame()
    {
        
        var expected = Convert.FromHexString("010c0c004fe41193f20600008a00");
        var body = ActorStatePackets.BuildOnMountFarGlider(0x06F29311u, 0x008A);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void NfySkillMoveCancel2747_ReproducesRetailFrame()
    {
        
        var expected = Convert.FromHexString("0110100060e40000000011a91c0786d70100");
        var body = ActorStatePackets.BuildNfySkillMoveCancel(0x071CA911u, 0x0001D786u);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void NfySkillMoveEnd2749_ReproducesRetailFrame()
    {
        
        
        var expected = Convert.FromHexString(
            "0121210061e40000000013f09d01000000000ea4202444144eaa434d6a3a427e27d3be");
        var body = ActorStatePackets.BuildNfySkillMoveEnd(
            0x019DF013u, F(0x442420A4u), F(0x43AA4E14u), F(0x423A6A4Du), F(0xBED3277Eu));
        Assert.Equal(expected, body);
    }

    
    public static IEnumerable<object[]> AllBuilders()
    {
        yield return new object[] { ActorStatePackets.BuildOnSitDown(0x11223344u, 1) };
        yield return new object[] { ActorStatePackets.BuildOnEmoticonAni(0x11223344u, 5u) };
        yield return new object[] { ActorStatePackets.BuildOnRevival(0x11223344u, 1f, 2f, 3f) };
        yield return new object[] { ActorStatePackets.BuildOnAckTransModeStart() };
        yield return new object[] { CombatSkillNotifyPackets.BuildSkillChannelingCancelNotify2585(0x11223344u, 7u) };
        yield return new object[] { ActorStatePackets.BuildOnSkillChannelingCompleteNotify(0x11223344u, 7u) };
        yield return new object[] { ActorStatePackets.BuildNfyRideRidingPet(1u, 2u, 3u) };
        yield return new object[] { ActorStatePackets.BuildOnMountFarGlider(0x11223344u, 9) };
        yield return new object[] { ActorStatePackets.BuildNfySkillMoveCancel(0x11223344u, 8u) };
        yield return new object[] { ActorStatePackets.BuildNfySkillMoveEnd(0x11223344u, 1f, 2f, 3f, 4f) };
    }

    [Theory]
    [MemberData(nameof(AllBuilders))]
    public void FirstByteIsAValidCompactMarker(byte[] body)
    {
        Assert.NotEmpty(body);
        byte marker = body[0];
        Assert.True(marker == 1 || marker == 2 || marker == 4 || marker == 8,
            $"first body byte {marker} is not a compact marker in {{1,2,4,8}}");
    }

    [Theory]
    [MemberData(nameof(AllBuilders))]
    public void SelfLenEqualsTwoPlusPayloadLength(byte[] body)
    {
        
        Assert.Equal(1, (int)body[0]);
        int selfLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
        int payloadLen = body.Length - 4;
        Assert.Equal(2 + payloadLen, selfLen);
        Assert.Equal(selfLen, (int)body[1]); 
    }
}
