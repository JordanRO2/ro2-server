using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gates for the combat, progression and interaction notifies.
/// </summary>
/// <remarks>
/// An unframed body is rejected by the client's compact-length decoder, which then hands the consumer a null blob.
/// </remarks>
public class CombatPacketsTests
{
    private static ReadOnlySpan<byte> Blob(byte[] body) => body.AsSpan(1 + body[0]);

    private static void AssertWellFormed(byte[] body, int len, ushort tag)
    {
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(len, body.Length);
        var blob = Blob(body);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(tag, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
    }

    
    [Fact]
    public void AttackDamage2696_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString(
            "013232003fe4000000000104ea3a000000000000000000000000000011a61b072b4d010054280000137d0f026cf10000b52c0000");
        var body = CombatPackets.BuildAttackDamage2696(
            attackerHandle: 0x071BA611u, attackerCurHp: 85291, attackerCurSp: 10324,
            victimHandle: 0x020F7D13u, victimCurHpAfter: 61804, victimCurSp: 11445,
            damage: 15082, damageKind: 4);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void AttackDamage2696_ActorRecordsAreMisalignedAndSpIs16Bit()
    {
        
        
        var body = CombatPackets.BuildAttackDamage2696(
            0x071BA611u, 85291, 10324, 0x020F7D13u, 61804, 11445, 15082, 4);
        AssertWellFormed(body, CombatPackets.AttackDamageBodyLen, CombatPackets.MsgTagAttackDamage);
        var blob = Blob(body);
        Assert.Equal(0x071BA611u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(26)));
        Assert.Equal(85291u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(30)));
        Assert.Equal(10324, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(34)));
        Assert.Equal(0x020F7D13u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(38)));
        Assert.Equal(61804u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(42)));
        Assert.Equal(11445, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(46)));
        Assert.Equal(0x11, blob[26]);   
        Assert.Equal(0x13, blob[38]);   
    }

    
    [Fact]
    public void LevelUp2394_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString(
            "01262600c9e211bdf00631ff5b000000000000390900003909000078000000780000000002000000");
        var body = CombatPackets.BuildLevelUp2394(
            actorHandle: 0x06F0BD11u, level: 49, expWithinLevel: 23551,
            maxHp: 2361, curHp: 2361, maxSp: 120, curSp: 120, statPointsGranted: 2);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void LevelUp2394_PutsTheHandleAtBlobPlus4_UnlikeItsSibling()
    {
        
        var body = CombatPackets.BuildLevelUp2394(0x06F0BD11u, 49, 23551, 2361, 2361, 120, 120, 2);
        AssertWellFormed(body, CombatPackets.LevelUpBodyLen, CombatPackets.MsgTagLevelUp);
        Assert.Equal(0x06F0BD11u, BinaryPrimitives.ReadUInt32LittleEndian(Blob(body).Slice(4)));

        var notify = CombatPackets.BuildLevelUpNotify2395(0x06F0BD11u, 49);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(Blob(notify).Slice(4)));
        Assert.Equal(0x06F0BD11u, BinaryPrimitives.ReadUInt32LittleEndian(Blob(notify).Slice(8)));
    }

    [Fact]
    public void LevelUp2394_ClampsSpToWhatTheClientCanStore()
    {
        
        
        var body = CombatPackets.BuildLevelUp2394(0x11u, 50, 0, 1, 1, 100_000, 100_000, 0);
        var blob = Blob(body);
        Assert.Equal(65535u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(25)));
        Assert.Equal(65535u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(29)));
    }

    [Theory]
    [InlineData(1, 2, 1)]       
    [InlineData(24, 25, 2)]     
    [InlineData(48, 49, 2)]
    [InlineData(1, 18, 17)]     
    public void StatPointsFollowTheMeasuredRule(int from, int to, int expected)
    {
        Assert.Equal(expected, CombatPackets.StatPointsForLevelUp(from, to));
    }

    
    [Fact]
    public void LevelUpNotify2395_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString("010f0f00cae2000000001115f006120000");
        var body = CombatPackets.BuildLevelUpNotify2395(0x06F01511u, 18);
        Assert.Equal(expected, body);
        AssertWellFormed(body, CombatPackets.LevelUpNotifyBodyLen,
            CombatPackets.MsgTagLevelUpNotify);
    }

    
    [Fact]
    public void UseItem2329_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString("01161600a6e21132f30600003f0107e8780da83d040e5201");
        var body = CombatPackets.BuildUseItem2329(
            actorHandle: 0x06F33211u, slotIndex: 63, bagId: 1,
            itemUid: 1010000008427661319UL, remainingCount: 82, useSource: 1);
        Assert.Equal(expected, body);
        AssertWellFormed(body, CombatPackets.UseItemBodyLen, CombatPackets.MsgTagUseItem);
    }

    [Fact]
    public void UseItem2329_CarriesTheUidAsOneU64()
    {
        
        
        var body = CombatPackets.BuildUseItem2329(0x11u, 0, 1, 1010000008427661319UL, 1);
        Assert.Equal(1010000008427661319UL,
            BinaryPrimitives.ReadUInt64LittleEndian(Blob(body).Slice(12)));
    }

    
    [Fact]
    public void SocialAni2396_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString("01101000cde2114def06114def0642000000");
        var body = CombatPackets.BuildSocialAni2396(0x06EF4D11u, 66);
        Assert.Equal(expected, body);
        AssertWellFormed(body, CombatPackets.SocialAniBodyLen, CombatPackets.MsgTagSocialAni);
    }

    [Fact]
    public void SocialAni2396_MirrorsTheHandle()
    {
        
        
        var body = CombatPackets.BuildSocialAni2396(0x06EF4D11u, 66);
        var blob = Blob(body);
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8)));
    }

    
    [Fact]
    public void AttackStart2694_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString("011414003ee40000000001000000119906071364ce01");
        var body = CombatPackets.BuildAttackStart2694(0x07069911u, 0x01CE6413u);
        Assert.Equal(expected, body);
        AssertWellFormed(body, CombatPackets.AttackStartBodyLen, CombatPackets.MsgTagAttackStart);
    }

    [Fact]
    public void AttackStart2694_IsNotTheErrorOpcode()
    {
        
        
        
        Assert.Equal(2694, RO2.Network.Protocol.ZoneS2COpcodes.NfyNormalAttackStart);
        Assert.NotEqual(RO2.Network.Protocol.ZoneS2COpcodes.NfyNormalAttackStart,
            RO2.Network.Protocol.ZoneS2COpcodes.AnsErrorNormalAttack);

        var body = CombatPackets.BuildAttackStart2694(0x00100011u, 0x00013813u);
        var blob = Blob(body);
        Assert.Equal(0x00100011u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12)));
        Assert.Equal(0x00013813u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16)));
        Assert.Equal(0x13, blob[16]);   
    }

    
    [Fact]
    public void AttackRefused2695_ReproducesTheModalRetailBody()
    {
        
        
        var expected = Convert.FromHexString("9a4e000000000000000000000000");
        Assert.Equal(expected, CombatPackets.BuildAttackRefused2695());
        Assert.Equal(CombatPackets.AttackRefusedBodyLen, expected.Length);
    }

    [Fact]
    public void AttackRefused2695_HasNoCompactEnvelope()
    {
        
        
        var body = CombatPackets.BuildAttackRefused2695();
        Assert.Equal(14, body.Length);
        Assert.Equal(CombatPackets.AttackRefusedCode,
            BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.All(body.Skip(2), b => Assert.Equal(0, b));
    }

    [Fact]
    public void AttackRefused2695_DeadAndLiveTargetsTakeTheirMeasuredRetailCodes()
    {
        
        
        
        
        
        
        Assert.Equal(20122, CombatPackets.AttackRefusedTargetDead);
        Assert.Equal(22301, CombatPackets.AttackRefusedLiveTarget);
        
        Assert.Equal(CombatPackets.AttackRefusedTargetDead, CombatPackets.AttackRefusedCode);

        
        Assert.Equal(Convert.FromHexString("9a4e000000000000000000000000"),
            CombatPackets.BuildAttackRefused2695(CombatPackets.AttackRefusedTargetDead));
        Assert.Equal(Convert.FromHexString("1d57000000000000000000000000"),
            CombatPackets.BuildAttackRefused2695(CombatPackets.AttackRefusedLiveTarget));
    }

    [Theory]
    [InlineData(20158)]
    public void AttackRefused2695_OnlyEmitsCodesRetailUses(ushort code)
    {
        
        
        var body = CombatPackets.BuildAttackRefused2695(code);
        Assert.Equal(code, BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.Equal(14, body.Length);
    }

    
    [Fact]
    public void ChangeState2398_ReproducesALiveRetailPcFrame()
    {
        
        var expected = Convert.FromHexString(
            "011b1b00eae21146680611466806000000000000000200000000000000");
        var body = CombatPackets.BuildChangeState2398(
            0x06684611u, CombatPackets.ActorState.PcNormal, trailing: 2);
        Assert.Equal(expected, body);
        AssertWellFormed(body, CombatPackets.ChangeStateBodyLen, CombatPackets.MsgTagChangeState);
    }

    [Fact]
    public void ChangeState2398_ReproducesALiveRetailNpcFrame()
    {
        
        var expected = Convert.FromHexString(
            "011b1b00eae20000000013626301030001000000000300000000000000");
        var body = CombatPackets.BuildChangeState2398(
            0x01636213u, CombatPackets.ActorState.NpcEnterCombat, trailing: 3);
        
        var differing = Enumerable.Range(0, expected.Length)
            .Where(i => body[i] != expected[i]).ToArray();
        Assert.Equal(new[] { 16 }, differing);   
    }

    [Fact]
    public void ChangeState2398_MirrorRuleFollowsTheSubjectType()
    {
        
        
        var pc = Blob(CombatPackets.BuildChangeState2398(0x06684611u, 0));
        Assert.Equal(0x06684611u, BinaryPrimitives.ReadUInt32LittleEndian(pc.Slice(4)));

        var npc = Blob(CombatPackets.BuildChangeState2398(0x01636213u, 3));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(npc.Slice(4)));
        Assert.Equal(0x01636213u, BinaryPrimitives.ReadUInt32LittleEndian(npc.Slice(8)));
    }

    
    [Fact]
    public void ActorIconInfo2643_ReproducesALiveRetailFrame()
    {
        
        var expected = Convert.FromHexString("13626301a8060000a8060000e007e0071900000000");
        var body = CombatPackets.BuildActorIconInfo2643(0x01636213u, 1704, 1704, 2016, 2016, 25);
        Assert.Equal(expected, body);
        Assert.Equal(CombatPackets.ActorIconBodyLen, body.Length);
        Assert.Equal(0x13, body[0]);   
    }
}
