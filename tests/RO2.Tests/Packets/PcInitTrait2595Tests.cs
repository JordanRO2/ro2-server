using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the 2595 OnPcInitTrait builder (TraitPackets.BuildPcInitTrait2595) and the 205-byte 2310 head writer
/// (WorldEntryService.WriteCharacterHead205), checked against reference frames.
/// </summary>
public class PcInitTrait2595Tests
{
 /// <summary>The fixed 2595 sent at login, before the packet was built from the character.</summary>
    private static readonly byte[] LoginDonor =
    {
        0x02, 0x8C, 0x00, 0x8C, 0x00, 0x96, 0xE3, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01,
        0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x80, 0x3F, 0xE0, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    private static byte[] Trait2498(uint hp, ushort sp) => TraitPackets.BuildPcTrait2498(hp, sp, hp, sp);

    [Fact]
    public void NoBonusAndLevelOneVitals_ReproduceTheOldLoginDonor()
    {
        
        var built = TraitPackets.BuildPcInitTrait2595(new Character { Name = "T" }, Trait2498(224, 15));
        Assert.Equal(LoginDonor, built);
    }

    [Fact]
    public void VitAllocation_MatchesRetailAnswerTo1493()
    {
        
        
        var retail = Convert.FromHexString(
            "028c008c0096e30000000000000000000001000100010001007801000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000803ff45c01003324000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
        var c = new Character { Name = "T", BonusVit = 375 };
        Assert.Equal(retail, TraitPackets.BuildPcInitTrait2595(c, Trait2498(89332, 9267)));
    }

    [Fact]
    public void StrAllocation_MatchesRetailAnswerTo1493()
    {
        
        var retail = Convert.FromHexString(
            "028c008c0096e30000000000000000000078010100010001000100000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000803fe6e90000f825000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
        var c = new Character { Name = "T", BonusStr = 375 };
        Assert.Equal(retail, TraitPackets.BuildPcInitTrait2595(c, Trait2498(59878, 9720)));
    }

    [Fact]
    public void Head205_IsTheFirst205BytesOfThe2310Body()
    {
        var c = new Character
        {
            Name = "HeadCheck", Level = 37, Exp = 123456, Job = 3, JobLevel = 12, JobExp = 999,
            MasterLevel = 2, Gold = 4_000_000_000L, MapId = 1100,
        };
        using var full = new PacketWriter();
        WorldEntryService.WriteCharacterDetail(full, c, 0x0DE0B6B3A7F07304UL, 0x0137B811u, null, 1100u, 10f, 20f, 30f,
            talentPointsRemaining: 7);
        using var head = new PacketWriter();
        WorldEntryService.WriteCharacterHead205(head, c, 0x0DE0B6B3A7F07304UL, 0x0137B811u, 1100u, 10f, 20f, 30f, 7);

        Assert.Equal(205, head.Length);
        Assert.Equal(full.ToArray().AsSpan(4, 205).ToArray(), head.ToArray());   
    }
}
