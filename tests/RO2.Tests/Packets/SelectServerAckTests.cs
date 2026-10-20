using System.Buffers.Binary;
using System.Text;
using RO2.Network.Packets;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Byte-level tests for the SelectServerAck (RMI 12504) payload: [data_length:4][00 00][channel_entry][session_guid:16].</summary>
public class SelectServerAckTests
{
    private static byte[] BuildSelectServerAckPayload(
        uint channelId, byte flag, string channelName,
        string ip, ushort port, byte load, Guid sessionGuid)
    {
        using var writer = new PacketWriter();

        var lengthPos = writer.Reserve(4);
        var dataStart = writer.Length;

        
        writer.WriteUInt16(0);

        
        writer.WriteUInt32(channelId);
        writer.WriteByte(flag);
        writer.WriteUnicodeString(channelName);
        writer.WriteUnicodeString(ip);
        writer.WriteUInt16(port);
        writer.WriteByte(load);

        
        writer.WriteBytes(sessionGuid.ToByteArray());

        writer.WriteAt(lengthPos, (uint)(writer.Length - dataStart));

        return writer.ToArray();
    }

    [Fact]
    public void DataLengthPrefix_IsCorrect()
    {
        var guid = Guid.NewGuid();
        var payload = BuildSelectServerAckPayload(
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02, guid);

        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        Assert.Equal((uint)(payload.Length - 4), dataLength);
    }

    [Fact]
    public void MatchesCapture_DataLength76()
    {
        
        var guid = Guid.NewGuid();
        var payload = BuildSelectServerAckPayload(
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02, guid);

        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        
        Assert.Equal(76u, dataLength);
    }

    [Fact]
    public void UnknownBytes_AreZero()
    {
        var guid = Guid.NewGuid();
        var payload = BuildSelectServerAckPayload(
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02, guid);

        
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4)));
    }

    [Fact]
    public void ChannelId_AtCorrectOffset()
    {
        var guid = Guid.NewGuid();
        var payload = BuildSelectServerAckPayload(
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02, guid);

        
        var channelId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(6));
        Assert.Equal(0x06514041u, channelId);
    }

    [Fact]
    public void SessionGuid_Last16Bytes()
    {
        var guid = new Guid("D7714FC7-3598-4486-AABB-1735FCE1DE4E");
        var payload = BuildSelectServerAckPayload(
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02, guid);

        
        var guidBytes = payload.AsSpan(payload.Length - 16).ToArray();
        var recoveredGuid = new Guid(guidBytes);
        Assert.Equal(guid, recoveredGuid);
    }

    [Fact]
    public void MatchesCapture_ByteStructure()
    {
        
        var guid = Guid.NewGuid();
        var payload = BuildSelectServerAckPayload(
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02, guid);

        
        byte[] expectedStart = [
            0x4C, 0x00, 0x00, 0x00, 
            0x00, 0x00,             
            0x41, 0x40, 0x51, 0x06, 
            0x01,                   
            0x09, 0x00,             
            0x43, 0x00,             
        ];

        Assert.Equal(expectedStart, payload.AsSpan(0, expectedStart.Length).ToArray());
    }

    [Fact]
    public void Port_AtCorrectPosition()
    {
        var guid = Guid.NewGuid();
        var payload = BuildSelectServerAckPayload(
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02, guid);

        
        
        int offset = 4 + 2 + 4 + 1 + (2 + 18) + (2 + 28);
        Assert.Equal(61, offset);

        var port = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
        Assert.Equal(7201, port);

        
        Assert.Equal(0x21, payload[offset]);
        Assert.Equal(0x1C, payload[offset + 1]);
    }
}
