using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Crash gate for the quest, talent and pro-job S2C notifies that were once sent as raw bodies to compact-length opcodes.
/// </summary>
/// <remarks>
/// A raw body whose first byte is not a compact-length marker in {1, 2, 4, 8} makes the client's compact decoder return an empty buffer, and the per-opcode handler then dereferences it without a null check. Every builder here wraps its payload with <see cref="RO2.Network.Packets.PacketWriter.WriteCompactBABody"/>. The tests assert that the first body byte is a valid marker, that the u16 selfLen field equals 2 + payloadLen, that the body length and msgTag match the protocol, and that each body matches a reference frame wherever every varying field can be set from the arguments.
/// </remarks>
public class QuestCrashSweepTests
{
    private static void AssertValidMarker(byte[] body)
    {
        Assert.True(body.Length >= 4, "body too short to hold marker+selfLen");
        Assert.True(body[0] is 1 or 2 or 4 or 8, $"first body byte {body[0]:x2} is not a compact marker");
    }

    
    private static void AssertEnvelope(byte[] body, ushort expectedMsgTag, int expectedBodyLen)
    {
        AssertValidMarker(body);
        Assert.Equal(0x01, (int)body[0]);                          
        Assert.Equal(expectedBodyLen, body.Length);                 
        ushort selfLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        
        Assert.Equal(body.Length - 2, (int)selfLen);
        Assert.Equal((int)body[1], (int)selfLen);                   
        ushort msgTag = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4, 2));
        Assert.Equal(expectedMsgTag, msgTag);
    }

    
    [Fact]
    public void QuestGiveup2420_ReproducesRealFrame()
    {
        
        var expected = Convert.FromHexString("01111100b8e30000000024ba9d060000000000");
        var body = QuestProgressPackets.BuildQuestGiveup2420(0x069DBA24u);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void QuestInsert2421_ReproducesRealFrame()
    {
        
        var expected = Convert.FromHexString("01101000b5e300000000d8d4540700000000");
        var body = QuestProgressPackets.BuildQuestInsert2421(0x0754D4D8u);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void QuestError2431_ReproducesRealFrame()
    {
        
        var expected = Convert.FromHexString("01141400c3e300000000dad45407c754000000000000");
        var body = QuestProgressPackets.BuildQuestError2431(0x0754D4DAu, 0x54C7);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void UseTalentPoint2543_ReproducesRealFrame()
    {
        
        
        
        
        
        
        
        var expected = Convert.FromHexString("011414000fe400000000000008010000001910000005");
        var body = QuestProgressPackets.BuildUseTalentPoint2543(
            new List<(uint, byte)> { (0x1019u, 5) }, remainingPoints: 8);
        Assert.Equal(expected, body);
    }

    [Theory]
    
    [InlineData(22, 1)]
    [InlineData(27, 2)]
    [InlineData(32, 3)]
    [InlineData(37, 4)]
    public void UseTalentPoint2543_LengthModelHolds(int bodyLen, int count)
    {
        var allocs = new List<(uint, byte)>();
        for (int i = 0; i < count; i++) allocs.Add(((uint)(0x1000 + i), (byte)1));
        
        
        
        var body = QuestProgressPackets.BuildUseTalentPoint2543(allocs, remainingPoints: 7);
        Assert.Equal(bodyLen, body.Length);
        
        uint wireCount = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(13, 4));
        Assert.Equal((uint)count, wireCount);
    }

    
    [Fact]
    public void QuestGiveup2420_Envelope() =>
        AssertEnvelope(QuestProgressPackets.BuildQuestGiveup2420(123), 58296, 19);

    [Fact]
    public void QuestInsert2421_Envelope() =>
        AssertEnvelope(QuestProgressPackets.BuildQuestInsert2421(123), 58293, 18);

    [Fact]
    public void QuestMonsterOrGo2425_Envelope() =>
        AssertEnvelope(QuestProgressPackets.BuildQuestMonsterOrGo2425(123, 10010000, 1), 58331, 20);

    [Fact]
    public void QuestPrize2429_Envelope() =>
        AssertEnvelope(QuestProgressPackets.BuildQuestPrize2429(123), 58303, 30);

    [Fact]
    public void QuestError2431_Envelope() =>
        AssertEnvelope(QuestProgressPackets.BuildQuestError2431(123, 1), 58307, 22);

    
    
    
    
    
    [Fact]
    public void InitTalentPoint2544_Envelope() =>
        AssertEnvelope(QuestProgressPackets.BuildInitTalentPoint2544(0x11223344, 42), 58385, 13);

    [Fact]
    public void BonusStatPoint2680_Envelope() =>
        AssertEnvelope(QuestProgressPackets.BuildBonusStatPoint2680(0x11223344, 0, ReadOnlySpan<byte>.Empty), 58423, 32);

    [Fact]
    public void ChangeProJob2682_Envelope() =>
        AssertEnvelope(QuestProgressPackets.BuildChangeProJob2682(0x11223344, 3, 1), 58426, 16);

    [Fact]
    public void ChangeProJobInfo2683_Envelope() =>
        AssertEnvelope(QuestProgressPackets.BuildChangeProJobInfo2683(2), 58425, 21);

    [Fact]
    public void UseTalentPoint2543_MarkerAndSelfLen()
    {
        
        
        var body = QuestProgressPackets.BuildUseTalentPoint2543(
            new List<(uint, byte)> { (7u, 2), (9u, 3) }, remainingPoints: 5);
        AssertValidMarker(body);
        Assert.Equal(0x01, (int)body[0]);
        ushort selfLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        Assert.Equal(body.Length - 2, (int)selfLen);
        ushort msgTag = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4, 2));
        Assert.Equal((ushort)58383, msgTag);
    }
}
