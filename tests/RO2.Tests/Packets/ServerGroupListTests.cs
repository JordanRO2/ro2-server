using System.Buffers.Binary;
using System.Text;
using RO2.Network.Packets;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Byte-level tests for the ServerGroupList (RMI 12503) payload: [data_length:4][server_count:2][server_entries][channel_count:2][channel_entries].</summary>
public class ServerGroupListTests
{
 /// <summary>Builds a ServerGroupList payload, using the same logic as LoginService.SendServerGroupListAsync.</summary>
    private static byte[] BuildServerGroupListPayload(
        uint serverId, string serverName,
        uint channelId, byte flag, string channelName,
        string ip, ushort port, byte load)
    {
        using var writer = new PacketWriter();

        
        var lengthPos = writer.Reserve(4);
        var dataStart = writer.Length;

        
        writer.WriteUInt16(1);

        
        writer.WriteUInt32(serverId);
        writer.WriteUnicodeString(serverName);

        
        writer.WriteUInt16(1);

        
        writer.WriteUInt32(channelId);
        writer.WriteByte(flag);
        writer.WriteUnicodeString(channelName);
        writer.WriteUnicodeString(ip);
        writer.WriteUInt16(port);
        writer.WriteByte(load);

        
        writer.WriteAt(lengthPos, (uint)(writer.Length - dataStart));

        return writer.ToArray();
    }

    [Fact]
    public void DataLengthPrefix_IsCorrect()
    {
        var payload = BuildServerGroupListPayload(
            0x00C10001, "Odin",
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02);

        
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        Assert.Equal((uint)(payload.Length - 4), dataLength);
    }

    [Fact]
    public void ServerCount_IsOne()
    {
        var payload = BuildServerGroupListPayload(
            0x00C10001, "Odin",
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02);

        
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4));
        Assert.Equal(1, count);
    }

    [Fact]
    public void ServerId_MatchesCapture()
    {
        var payload = BuildServerGroupListPayload(
            0x00C10001, "Odin",
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02);

        
        var serverId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(6));
        Assert.Equal(0x00C10001u, serverId);
    }

    [Fact]
    public void ServerName_IsProudNetWideString()
    {
        var payload = BuildServerGroupListPayload(
            0x00C10001, "Odin",
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02);

        
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10));
        Assert.Equal(4, nameLen);

        
        var nameBytes = payload.AsSpan(12, 8);
        var name = Encoding.Unicode.GetString(nameBytes);
        Assert.Equal("Odin", name);
    }

    [Fact]
    public void MatchesCapture_First20Bytes()
    {
        var payload = BuildServerGroupListPayload(
            0x00C10001, "Odin",
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02);

        
        
        
        byte[] expected = [
            0x01, 0x00,             
            0x01, 0x00, 0xC1, 0x00, 
            0x04, 0x00,             
            0x4F, 0x00, 0x64, 0x00, 0x69, 0x00, 0x6E, 0x00  
        ];

        
        Assert.Equal(expected, payload.AsSpan(4, expected.Length).ToArray());
    }

    [Fact]
    public void ChannelCount_AfterServerEntry()
    {
        var payload = BuildServerGroupListPayload(
            0x00C10001, "Odin",
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02);

        
        var channelCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(20));
        Assert.Equal(1, channelCount);
    }

    [Fact]
    public void ChannelEntry_MatchesCaptureFormat()
    {
        var payload = BuildServerGroupListPayload(
            0x00C10001, "Odin",
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02);

        
        int offset = 22;

        
        var channelId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
        Assert.Equal(0x06514041u, channelId);
        offset += 4;

        
        Assert.Equal(0x01, payload[offset]);
        offset += 1;

        
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
        Assert.Equal(9, nameLen); 
        offset += 2;
        var name = Encoding.Unicode.GetString(payload.AsSpan(offset, nameLen * 2));
        Assert.Equal("Channel 1", name);
        offset += nameLen * 2;

        
        var ipLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
        Assert.Equal(14, ipLen); 
        offset += 2;
        var ip = Encoding.Unicode.GetString(payload.AsSpan(offset, ipLen * 2));
        Assert.Equal("128.241.93.211", ip);
        offset += ipLen * 2;

        
        var port = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
        Assert.Equal(7201, port);
        offset += 2;

        
        Assert.Equal(0x02, payload[offset]);
        offset += 1;

        
        Assert.Equal(payload.Length, offset);
    }

    [Fact]
    public void ChannelCount_MatchesCaptureOffset()
    {
        
        
        var payload = BuildServerGroupListPayload(
            0x00C10001, "Odin",
            0x06514041, 0x01, "Channel 1",
            "128.241.93.211", 7201, 0x02);

        
        
        var offset = 20;
        var channelCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
        Assert.Equal(1, channelCount);

        
        var firstChannelId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(22));
        Assert.Equal(0x06514041u, firstChannelId);
    }
}
