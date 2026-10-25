using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Checks the 2315 OnPCViewData record built from a live player (ActorStatePackets.BuildOnPCViewData2315(PlayerState,
/// Character, equipped)) against the 2504 player record it wraps.
/// </summary>
public class OnPcViewData2315RecordTests
{
    
    
    
    private const string RetailPsStorage2315 =
        "02eb00eb0097e200000000115c700b" +                                   
        "115c700b6b7df4a7b3b6e00d0000" +                                     
        "33e33344710d3844a4705e42" +                                         
        "01010602ff0002000000030fff0001" +                                   
        "19090000190900005a0000005a000000" +                                 
        "0000a040" + "920a063f" +                                            
        "10008500" + "00" +                                                  
        "09" + "50005300530074006f007200610067006500" +                      
        "40104406" +                                                         
        "0002" + "02000000" + "00" + "00" +                                  
        "0400" +                                                             
        "02f935bd000000000000000000000000000000000000000000000000" +
        "033978cc000000000000000000000000000000000000000000000000" +
        "0479badb000000000000000000000000000000000000000000000000" +
        "05b9fcea000000000000000000000000000000000000000000000000" +
        "0000" + "00000000" + "0000";                                         

 /// <summary>Body offset of the move speed field: 3-byte compact prefix, 12-byte envelope, then the field.</summary>
    private const int SpeedOffset = 3 + 12 + 57;

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <param name="job">Character.Job. The default models a freshly created character.</param>
    private static (PlayerState, Character, List<InventoryItem>) PsStorage(byte job = 1, byte create21 = 1)
    {
        var raw = new byte[23];
        Convert.FromHexString("01010602ff0002000000030fff0001").CopyTo(raw, 0);  
        raw[19] = 4;          
        raw[21] = create21;   
        var c = new Character
        {
            Name = "PSStorage", Level = 50, JobLevel = 1, Gender = 1, Race = 1, Job = job, EquipHideFlag = 133,
            AllowAnyoneReview = true,
            AppearanceJson = CharacterSerializer.ToJson(new CharacterAppearance { RawBytes = raw }),
        };
        var head = Convert.FromHexString(RetailPsStorage2315);
        float F(int recOff) => BinaryPrimitives.ReadSingleLittleEndian(head.AsSpan(15 + recOff, 4));
        var p = new PlayerState
        {
            CharacterId = 0x5053_7701,   
            EntityId = 0x0b705c11,
            CharacterUid = 0x0de0b6b3a7f47d6b,
            Name = "PSStorage",
            Character = c,
            MapId = 1100,
            X = F(14), Y = F(18), Z = F(22),
            Rotation = F(61),
            Hp = 2329, MaxHp = 2329, Mp = 90, MaxMp = 90,
        };
        var worn = new List<InventoryItem>
        {
            new() { Id = 1, StorageType = 1, Slot = 2, ItemId = 0x00bd35f9 },
            new() { Id = 2, StorageType = 1, Slot = 3, ItemId = 0x00cc7839 },
            new() { Id = 3, StorageType = 1, Slot = 4, ItemId = 0x00dbba79 },
            new() { Id = 4, StorageType = 1, Slot = 5, ItemId = 0x00eafcb9 },
        };
        return (p, c, worn);
    }

    [Fact]
    public void SelfRecord_IsRetailByteForByte_ExceptTheMoveSpeed()
    {
        var (p, c, worn) = PsStorage();

        var body = ActorStatePackets.BuildOnPCViewData2315(p, c, worn, TestEmblems.Create());

        
        var expected = Convert.FromHexString(RetailPsStorage2315);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(SpeedOffset, 4), TraitPackets.PcModalWorldEntrySpeed);
        Assert.Equal(Hex(expected), Hex(body));
    }

    [Fact]
    public void Record_IsThePcRecordOur2504Carries()
    {
        var (p, c, worn) = PsStorage();
        using var rec = new PacketWriter();
        PlayerSpawnService.WriteRecord(rec, p, c, TestEmblems.Create(), worn);

        var body = ActorStatePackets.BuildOnPCViewData2315(p, c, worn, TestEmblems.Create());

        Assert.Equal(Hex(rec.GetWrittenSpan()), Hex(body.AsSpan(3 + 12)));
    }

 /// <summary>
 /// The active-effect list. The single element is a permanent effect (40000).
 /// </summary>
    [Fact]
    public void Vec23_CarriesTheActiveEffects_NotTheWornItems()
    {
        var (p, c, worn) = PsStorage();
        p.EntityId = 0x0b6e0c11;
        p.ActiveEffects.Add(new ActiveEffect
        {
            EffectId = 40000, EffectValue = 1, StackCount = 1, SourceSkillId = 4000001,
            CasterHandle = 0x0b6e0c11, TargetHandle = 0x0b6e0c11, ExpiresAt = null,
        });

        var body = ActorStatePackets.BuildOnPCViewData2315(p, c, worn, TestEmblems.Create());

        
        var tail = body.AsSpan(body.Length - (2 + 23 + 4 + 2));
        Assert.Equal("0100" + "409c0000010001000000000000000001093d00110c6e0b" + "00000000" + "0000", Hex(tail));
        
        var vec28 = body.AsSpan(body.Length - (2 + 23 + 4 + 2) - (2 + 4 * 28), 2);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(vec28));
    }

    [Fact]
    public void RefusedReviews_SetMaskBit15_AsThe2504Does()
    {
        var (p, c, worn) = PsStorage();
        c.AllowAnyoneReview = false;

        var body = ActorStatePackets.BuildOnPCViewData2315(p, c, worn, TestEmblems.Create());

        
        int mask = 3 + 12 + 70 + 1 + 2 * "PSStorage".Length + 4;
        Assert.Equal("0082" + "02000000" + "00" + "00", Hex(body.AsSpan(mask, 8)));
    }

 /// <summary>
 /// jobBits packs the combat class (bits 16-20) and the creation profession (bits 12-15), which the 2310 also sends.
 /// For a level 50 character, encodes level 50, class 17, profession 4 and job level 50.
 /// </summary>
    [Fact]
    public void JobBits_CarryClassAndProfession_NotJobTwice()
    {
        var (p, c, worn) = PsStorage(job: 17, create21: 4);   
        c.JobLevel = 50;

        var body = ActorStatePackets.BuildOnPCViewData2315(p, c, worn, TestEmblems.Create());

        int jobBits = 3 + 12 + 70 + 1 + 2 * "PSStorage".Length;
        Assert.Equal(0x06514c80u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(jobBits, 4)));
    }

    [Fact]
    public void LooseValueOverload_NoLongerWritesWornItemsAsEffects()
    {
        var (p, c, worn) = PsStorage();

        var body = ActorStatePackets.BuildOnPCViewData2315(p.EntityId, p.CharacterUid, c.Name, c.Race, c.Gender,
            p.X, p.Y, p.Z, p.Rotation, 2329, 2329, 90, 90, c.Level, c.Job, c.JobLevel, worn);

        
        Assert.Equal("0000" + "00000000" + "0000", Hex(body.AsSpan(body.Length - 8)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(body.Length - 8 - 4 * 28 - 2, 2)));
    }
}
