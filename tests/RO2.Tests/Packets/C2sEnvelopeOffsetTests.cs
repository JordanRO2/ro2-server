using System.Buffers.Binary;
using RO2.Network.Packets;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Regression tests for C2S handlers that read the message tag as a data field.
/// </summary>
/// <remarks>
/// Enveloped C2S bodies use this layout (1-byte compact marker):
/// body+0 marker in {1,2,4,8}
/// body+1 compact length byte
/// body+2 u16 self length, equal to body.Length - 2
/// body+4 u16 message tag, a per-opcode constant in the 52001..58524 range (not data)
/// body+6 u32 self PC actor handle
/// body+10 first message-specific field
///
/// Earlier handlers skipped 4 bytes and read a u32, which placed their first field read on the constant
/// message tag at body+4. Each case asserts that body+4 holds the constant tag and that the field the fixed
/// handler reads sits at the chosen offset with the expected value.
/// </remarks>
public class C2sEnvelopeOffsetTests
{
 /// <summary>Asserts the envelope shape and that body+4 equals the given message tag.</summary>
    private static byte[] Enveloped(string hex, int expectedLen, ushort expectedMsgTag)
    {
        var body = Convert.FromHexString(hex);
        Assert.Equal(expectedLen, body.Length);
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });                       
        Assert.Equal(body.Length - 2, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2))); 
        Assert.Equal(expectedMsgTag, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));   
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)) & 0xFFu);    
        return body;
    }

    
    [Fact]
    public void UseTalentPoint1310()
    {
        var body = Enveloped("011212000ee411014b0605010000007110000005", 20, 58382);
        var r = new PacketReader(body.AsSpan());
        r.Skip(11);
        Assert.Equal(1u, r.ReadUInt32());       
        Assert.Equal(4209u, r.ReadUInt32());    
        Assert.Equal(5, r.ReadByte());          
    }

    
    [Fact]
    public void ChangeCostumeFlag1312()
    {
        var body = Enveloped("010a0a0020e411b346065e01", 12, 58400);
        var r = new PacketReader(body.AsSpan());
        r.Skip(10);
        Assert.Equal((ushort)350, r.ReadUInt16());
    }

    
    [Fact]
    public void CraftListLearn1348()
    {
        var body = Enveloped("010f0f0014e411b8940300131d00000a00", 17, 58388);
        Assert.Equal((byte)0x00, body[10]);
        var r = new PacketReader(body.AsSpan());
        r.Skip(11);
        Assert.Equal(7443u, r.ReadUInt32());   
        Assert.Equal((ushort)10, r.ReadUInt16()); 
    }

    
    [Fact]
    public void CraftItem1349()
    {
        var body = Enveloped("0111110015e41127f707001127f7075b060100", 19, 58389);
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(11)) & 0xFFu); 
        var r = new PacketReader(body.AsSpan());
        r.Skip(15);
        Assert.Equal((ushort)1627, r.ReadUInt16());
    }

    
    [Fact]
    public void TradeOpen1351()
    {
        var body = Enveloped("010c0c00b1e21166450611714506", 14, 58033);
        var r = new PacketReader(body.AsSpan());
        r.Skip(10);
        var targetHandle = r.ReadUInt32();
        Assert.Equal(0x11u, targetHandle & 0xFFu);   
        Assert.Equal(410993u, targetHandle >> 8);    
    }

    
    [Fact]
    public void TradeOpenConfirm1352()
    {
        var body = Enveloped("010d0d00b4e2116645060111714506", 15, 58036);
        var r = new PacketReader(body.AsSpan());
        r.Skip(10);
        Assert.Equal(1, r.ReadByte());
    }

    
    [Fact]
    public void EmoticonAnimation1370()
    {
        var body = Enveloped("010c0c00d7e311431d081a000000", 14, 58327);
        var r = new PacketReader(body.AsSpan());
        r.Skip(10);
        Assert.Equal(26u, r.ReadUInt32());
    }

    
    [Fact]
    public void Teleport1371()
    {
        var body = Enveloped("01101000e6e211626a0613b10e000c000000", 18, 58086);
        var r = new PacketReader(body.AsSpan());
        r.Skip(10);
        var gateHandle = r.ReadUInt32();
        Assert.Equal(0x13u, gateHandle & 0xFFu);   
        Assert.Equal(12u, r.ReadUInt32());         
    }

    
    [Fact]
    public void QuestAccept1374()
    {
        var body = Enveloped("01101000b4e311014b06d8d45407e44a5d05", 18, 58292);
        var r = new PacketReader(body.AsSpan());
        r.Skip(10);
        Assert.Equal(123000024u, r.ReadUInt32());
    }

    
    [Fact]
    public void QuestGiveUp1375()
    {
        var body = Enveloped("010c0c00b7e3112a050724ba9d06", 14, 58295);
        var r = new PacketReader(body.AsSpan());
        r.Skip(10);
        Assert.Equal(111000100u, r.ReadUInt32());
    }

    
    [Fact]
    public void QuestComplete1377()
    {
        var body = Enveloped("01101000dce311014b06dae84c3690442c04", 18, 58332);
        var r = new PacketReader(body.AsSpan());
        r.Skip(10);
        Assert.Equal(911010010u, r.ReadUInt32());
    }

    
    [Fact]
    public void QuestPrize1378()
    {
        var body = Enveloped("01141400bee311014b06e54a5d05d8d45407ffffffff", 22, 58302);
        
        Assert.NotEqual(123000024u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10)));
        var r = new PacketReader(body.AsSpan());
        r.Skip(14);
        Assert.Equal(123000024u, r.ReadUInt32());
    }

    
    [Fact]
    public void PartyInvite1382()
    {
        var body = Enveloped("013333001be31104c90662007500640064007900790000000000000000000000000000000000000000000000000000000000000000", 53, 58139);
        var r = new PacketReader(body.AsSpan());
        r.Skip(10);
        Assert.Equal("buddyy", r.ReadWideStringFixed(21).TrimEnd('\0'));
    }

    
    [Fact]
    public void PartyJoin1383()
    {
        var body = Enveloped("013c3c001ee311626a060000999fb5a7b3b6e00d42006c007500460061006900720065003900390000000000000000000000000000000000000000000000", 62, 58142);
        var r = new PacketReader(body.AsSpan());
        r.Skip(20);
        Assert.Equal("BluFaire99", r.ReadWideStringFixed(21).TrimEnd('\0'));
    }

    
    [Fact]
    public void NpcInteract1410()
    {
        var body = Enveloped("010c0c00f1e31104ce0614781500", 14, 58353);
        var r = new PacketReader(body.AsSpan());
        r.Skip(10);
        var handle = r.ReadUInt32();
        Assert.Equal(0x14u, handle & 0xFFu);   
        Assert.Equal(0x00157814u, handle);
    }
}
