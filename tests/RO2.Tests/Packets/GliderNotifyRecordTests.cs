using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// 2464 OnMountGliderNotify and 2465 OnUnmountGliderNotify, with the PC record our 2504 also sends, checked
/// against a reference 2465 frame, and the dismount position 1314 now uses.
/// </summary>
public class GliderNotifyRecordTests
{
    
    
    private const string RetailHeader =
        "11e81b07" + "81d9e7a7b3b6e00d" + "00" + "00"                   
        + "00806644" + "00609544" + "00004443"                        
        + "01" + "02" + "0907ff00010000000906ff0002"                  
        + "bae10000" + "bae10000" + "f8250000" + "f8250000"           
        + "0000d040" + "00000000"                                     
        + "01" + "00" + "7203" + "00";                                
    private const string RetailNameAndJobBits = "08" + "4e0061006d0061007a0075006b006f00" + "804c4d06";

    private static (PlayerState, Character) Namazuko()
    {
        var raw = new byte[23];
        Convert.FromHexString("01020907ff00010000000906ff0002").CopyTo(raw, 0);   
        raw[19] = 13;   
        raw[21] = 4;    
        var c = new Character
        {
            Name = "Namazuko", Level = 50, JobLevel = 50, Gender = 1, Race = 2, Job = 13, EquipHideFlag = 882,
            AppearanceJson = CharacterSerializer.ToJson(new CharacterAppearance { RawBytes = raw }),
        };
        var p = new PlayerState
        {
            CharacterId = 0x6E57_0465,   
            EntityId = 0x071be811,
            CharacterUid = 0x0de0b6b3a7e7d981,
            Name = "Namazuko",
            Character = c,
            MapId = 1100,
            X = 922f, Y = 1195f, Z = 196f,
            Rotation = 0f,
            Hp = 57786, MaxHp = 57786, Mp = 9720, MaxMp = 9720,
        };
        return (p, c);
    }

 /// <summary>[compact length][blob] -> blob.</summary>
    private static byte[] Blob(byte[] body)
    {
        int n = body[0];
        int len = n switch
        {
            1 => body[1],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1)),
            _ => (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1)),
        };
        Assert.Equal(body.Length - 1 - n, len);
        return body.AsSpan(1 + n).ToArray();
    }

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Unmount2465_RecordMatchesRetail_UpToJobBits()
    {
        var (p, c) = Namazuko();
        var blob = Blob(GliderNotifyPackets.BuildUnmountGliderNotify(p, c, Array.Empty<InventoryItem>(), TestEmblems.Create()));

        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));        
        Assert.Equal(0xE309, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(2)));   
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4)));

        
        
        
        
        
        var expected = Convert.FromHexString(RetailHeader + RetailNameAndJobBits);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(57), TraitPackets.PcModalWorldEntrySpeed);
        expected[65] = ActorStatePackets.DetailTailModelClass;
        Assert.Equal(Hex(expected), Hex(blob.AsSpan(8, expected.Length)));

        
        
        int mask = 8 + expected.Length;
        Assert.Equal(0x0200, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(mask)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(mask + 2)));
    }

    [Fact]
    public void Mount2464_CarriesTheMountIdBeforeTheRelation()
    {
        
        
        var (p, c) = Namazuko();
        var blob = Blob(GliderNotifyPackets.BuildMountGliderNotify(p, c, Array.Empty<InventoryItem>(), 0x2413, TestEmblems.Create()));
        Assert.Equal(0xE306, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(2)));   
        int mask = 8 + RetailHeader.Length / 2 + RetailNameAndJobBits.Length / 2;
        Assert.Equal(0x0280, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(mask)));
        Assert.Equal("1324000002000000", Hex(blob.AsSpan(mask + 2, 8)));
    }

    [Fact]
    public void Dismount_StandsTheRiderAtTheRouteEnd()
    {
        
        
        var route8 = new Spline { index = 8, smap_index = 1100, emap_index = 1100, end_x = 721f, end_y = 732f, end_z = 56f };
        Assert.Equal((721f, 732f, 56f), GetOffGliderHandler.DismountPosition(route8, 1100, 721.0001f, 732.0001f, 56.6f));

        
        Assert.Equal((1f, 2f, 3f), GetOffGliderHandler.DismountPosition(null, 1100, 1f, 2f, 3f));
        var crossMap = route8 with { emap_index = 1200 };
        Assert.Equal((1f, 2f, 3f), GetOffGliderHandler.DismountPosition(crossMap, 1100, 1f, 2f, 3f));
    }
}
