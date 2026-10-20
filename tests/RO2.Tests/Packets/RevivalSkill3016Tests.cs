using System.Buffers.Binary;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Covers revival by another player's skill: the 2718 map frame, the 3016 NfyRevivalSkill offer, the offer bookkeeping on PlayerState, and the burst that answers 1684.</summary>
public class RevivalSkill3016Tests
{
    private const uint Caster = 0x07FE5F11;
    private const uint DeadPc = 0x0812FB11;
    private const uint YggdrasilLeaf = 9200101;

 /// <summary>2718 as the dead PC received it (the caster's own copy is byte-identical).</summary>
    private const string Retail2718Hex =
        "0147" + "4700" + "4be4" + "00000000" + "e5618c00" + "115ffe07" + "01" + "11fb1208"
        + "000000000000000000000000" + "02" + "0000000000000000" + "0100" + "00" + "01"
        + "11fb1208" + "00000000" + "00000000" + "000000000000000000000000" + "00";

 /// <summary>The 3016 that followed it in the dead PC's stream, in the same millisecond.</summary>
    private const string Retail3016Hex = "23983744" + "223b3944" + "215e5d42" + "115ffe07" + "e5618c00";

 /// <summary>The dead PC's 1684 AnsRevivalSkill reply.</summary>
    private const string Retail1684Hex = "01" + "23983744223b3944215e5d42";

 /// <summary>The frames that answer that 1684, in order: 2399, 2398, 2497, 2401, with full HP and SP.</summary>
    private static readonly (ushort Opcode, string Hex)[] RetailBurst =
    {
        (2399, "011b1b00e9e2" + "11fb1208" + "11fb1208" + "0000" + "01" + "23983744223b3944215e5d42"),
        (2398, "011b1b00eae2" + "11fb1208" + "11fb1208" + "00" + "0000" + "00000000" + "02000000" + "00000000"),
        (2497, "01101000fce3" + "00000000" + "11fb1208" + "0000c040"),
        (2401, "011c1c00ebe2" + "11fb1208" + "11fb1208" + "51190100" + "28230000" + "51190100" + "28230000"),
    };

    private static (float X, float Y, float Z) Xyz(string hex, int offset)
    {
        var b = Convert.FromHexString(hex);
        return (BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset)),
                BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset + 4)),
                BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset + 8)));
    }

    private static PlayerState DeadPlayer(uint handle, uint sessionId, uint mapId = 1100) =>
        new() { EntityId = handle, SessionId = sessionId, MapId = mapId, Hp = 0, Mp = 0, IsDead = true };

    [Theory]
    [InlineData("23983744223b3944215e5d42115ffe07e5618c00", 0x07FE5F11u, 9200101u)] 
    [InlineData("032c38446d1f53441fd7b64111ca7c0de5618c00", 0x0D7CCA11u, 9200101u)] 
    [InlineData("47423544a7bd56441839c1411144290b9d830300", 0x0B294411u, 230301u)]  
    [InlineData("f968384434a35244f172b6411166ef0719cc0700", 0x07EF6611u, 511001u)]  
    public void Offer3016_ReproducesRetailFramesByteForByte(string retailHex, uint caster, uint skill)
    {
        var (x, y, z) = Xyz(retailHex, 0);

        var built = RevivalSkillPackets.BuildNfyRevivalSkill3016(x, y, z, caster, skill);

        Assert.Equal(Convert.FromHexString(retailHex), built);
        Assert.Equal(RevivalSkillPackets.NfyRevivalSkillBodyLen, built.Length);
    }

    [Fact]
    public void RevivalLanding2718_OnADeadPc_ReproducesRetailFrameByteForByte()
    {
        var caster = new PlayerState { EntityId = Caster, SessionId = 1, MapId = 1100, Hp = 5000 };
        var target = DeadPlayer(DeadPc, 2);
        Assert.True(RevivalSkillPackets.CanBeOffered(target, caster));

        var built = RevivalSkillPackets.BuildRevivalSkillDamage2718(
            Caster, YggdrasilLeaf, DeadPc, target, RevivalSkillPackets.RevivalCastTypeRetail);

        Assert.Equal(Convert.FromHexString(Retail2718Hex), built);
    }

    [Fact]
    public void RevivalLanding2718_SecondCasterOfTheSameDeath_ReproducesRetailFrame()
    {
        
        const string retail = "014747004be400000000e5618c0011ca7c0d0111cc7c0d000000000000000000000000020000000000000000"
                            + "0100000111cc7c0d000000000000000000000000000000000000000000";
        var built = RevivalSkillPackets.BuildRevivalSkillDamage2718(
            0x0D7CCA11, YggdrasilLeaf, 0x0D7CCC11, DeadPlayer(0x0D7CCC11, 2), 2);

        Assert.Equal(Convert.FromHexString(retail), built);
    }

    [Fact]
    public void RevivalLanding2718_OnAPcAlreadyRevived_CarriesTheEmptyElementRetailSends()
    {
        
        
        const string retail = "014747004be400000000e5618c00118fd0090111c2d009000000000000000000000000020000000000000000"
                            + "0100000100000000000000000000000000000000000000000000000000";
        var caster = new PlayerState { EntityId = 0x09D08F11, SessionId = 1, MapId = 1100, Hp = 5000 };
        var alive = new PlayerState { EntityId = 0x09D0C211, SessionId = 2, MapId = 1100, Hp = 50546, Mp = 6236 };
        Assert.False(RevivalSkillPackets.CanBeOffered(alive, caster));

        var built = RevivalSkillPackets.BuildRevivalSkillDamage2718(0x09D08F11, YggdrasilLeaf, 0x09D0C211, null, 2);

        Assert.Equal(Convert.FromHexString(retail), built);
    }

    [Fact]
    public void CanBeOffered_OnlyADeadPcOnTheCastersMapThatIsNotTheCaster()
    {
        var caster = new PlayerState { EntityId = Caster, SessionId = 1, MapId = 1100, Hp = 5000 };

        Assert.True(RevivalSkillPackets.CanBeOffered(DeadPlayer(DeadPc, 2), caster));
        Assert.True(RevivalSkillPackets.CanBeOffered(
            new PlayerState { EntityId = DeadPc, SessionId = 2, MapId = 1100, Hp = 0 }, caster));   
        Assert.False(RevivalSkillPackets.CanBeOffered(DeadPlayer(DeadPc, 2, mapId: 1200), caster));
        Assert.False(RevivalSkillPackets.CanBeOffered(DeadPlayer(Caster, 1), caster));
        Assert.False(RevivalSkillPackets.CanBeOffered(null, caster));
    }

    [Fact]
    public void Answer1684_ReproducesRetailBurstInOrderByteForByte()
    {
        var (x, y, z) = Xyz(Retail1684Hex, 1);

        var burst = RevivalSkillPackets.BuildRevivalBurst(DeadPc, x, y, z, 72017, 9000, 72017, 9000);

        Assert.Equal(RetailBurst.Length, burst.Count);
        for (var i = 0; i < burst.Count; i++)
        {
            Assert.Equal(RetailBurst[i].Opcode, burst[i].Opcode);
            Assert.Equal(Convert.FromHexString(RetailBurst[i].Hex), burst[i].Body);
        }
        Assert.Equal(new ushort[] { ZoneS2COpcodes.OnRevival, ZoneS2COpcodes.OnChangeState,
            ZoneS2COpcodes.OnChangeActorMoveSpeed, ZoneS2COpcodes.OnActorAutoHeal },
            burst.Select(f => f.Opcode).ToArray());
    }

    [Fact]
    public void The1684Echo_MatchesTheOfferItAnswers()
    {
        var target = DeadPlayer(DeadPc, 2);
        var (ox, oy, oz) = Xyz(Retail3016Hex, 0);
        target.AddRevivalOffer(new RevivalOffer(Caster, YggdrasilLeaf, ox, oy, oz));

        var (x, y, z) = Xyz(Retail1684Hex, 1);
        Assert.True(target.TryTakeRevivalOffer(x, y, z, out var offer));
        Assert.Equal(Caster, offer.CasterHandle);
        Assert.Equal(YggdrasilLeaf, offer.SkillId);
        Assert.Equal(0, target.RevivalOfferCount);
    }

    [Fact]
    public void AnEarlierOfferStaysAcceptable_AndTheRevivalSpendsAllOfThem()
    {
        
        
        var target = DeadPlayer(0x08597211, 2);
        var (ax, ay, az) = Xyz("b5b9a743e89280433c492b41", 0);
        var (bx, by, bz) = Xyz("9a6cac43ab2f7d433b492b41", 0);
        target.AddRevivalOffer(new RevivalOffer(0x08598411, YggdrasilLeaf, ax, ay, az));
        target.AddRevivalOffer(new RevivalOffer(0x085A3B11, YggdrasilLeaf, bx, by, bz));
        Assert.Equal(2, target.RevivalOfferCount);

        var (x, y, z) = Xyz("01b5b9a743e89280433c492b41", 1);
        Assert.True(target.TryTakeRevivalOffer(x, y, z, out var taken));
        Assert.Equal(0x08598411u, taken.CasterHandle);

        
        Assert.False(target.TryTakeRevivalOffer(bx, by, bz, out _));
    }

    [Fact]
    public void A1684NamingNoOffer_TakesNothing()
    {
        var target = DeadPlayer(DeadPc, 2);
        var (ox, oy, oz) = Xyz(Retail3016Hex, 0);
        target.AddRevivalOffer(new RevivalOffer(Caster, YggdrasilLeaf, ox, oy, oz));

        
        var nudged = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(ox) + 1);
        Assert.False(target.TryTakeRevivalOffer(nudged, oy, oz, out _));
        Assert.Equal(1, target.RevivalOfferCount);
    }

    [Fact]
    public void OffersAreDedupedCappedAndCleared()
    {
        var target = DeadPlayer(DeadPc, 2);
        var offer = new RevivalOffer(Caster, YggdrasilLeaf, 1f, 2f, 3f);
        target.AddRevivalOffer(offer);
        target.AddRevivalOffer(offer);
        Assert.Equal(1, target.RevivalOfferCount);

        for (var i = 0; i < PlayerState.MaxRevivalOffers + 4; i++)
            target.AddRevivalOffer(new RevivalOffer(Caster, YggdrasilLeaf, 10f + i, 2f, 3f));
        Assert.Equal(PlayerState.MaxRevivalOffers, target.RevivalOfferCount);
        
        Assert.True(target.TryTakeRevivalOffer(1f, 2f, 3f, out _));

        target.AddRevivalOffer(offer);
        target.ClearRevivalOffers();
        Assert.Equal(0, target.RevivalOfferCount);
    }

    [Fact]
    public void RevivalSkillGroups_AreTheShippedSkillFunction7Rows()
    {
        var groups = RevivalSkillPackets.ParseRevivalGroups(
            File.ReadAllBytes(Path.Combine(RequireDataDirectory(), "Skill_Table_Base.json")));

        
        Assert.Equal(new uint[] { 2303, 5110, 92001, 92002, 92003 }, groups.OrderBy(g => g).ToArray());
        
        IReadOnlySet<uint> set = groups;
        Assert.Contains(9200101u / 100, set);
        Assert.Contains(230301u / 100, set);
        Assert.Contains(511001u / 100, set);
        
        Assert.DoesNotContain(630310u / 100, set);
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
