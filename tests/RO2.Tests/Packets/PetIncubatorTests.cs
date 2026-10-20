using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for the 2926 AnsPCollectionIncubator response and the incubator rules behind requests 1630, 1631 and 1632.
/// The list layout is checked on every field the client reads. The padding after each date is written as 0.
/// Table rows are taken from IncubatorInfo.json and PetInfo.json.
/// </summary>
public class PetIncubatorTests
{
    private static readonly IncubatorInfo Basic = new()
    {
        Incubator_ID = 22402101, Incubator_Name = "Basic Incubator", Charge_Quantity = 2, Incubation_Speed = 10000,
    };
    private static readonly IncubatorInfo Cracked = new()
    {
        Incubator_ID = 22402103, Incubator_Name = "Cracked Incubator", Charge_Quantity = 1, Incubation_Speed = 5000,
    };
    private static readonly IncubatorInfo GoldenBound = new()
    {
        Incubator_ID = 22402112, Incubator_Name = "Golden Incubator [Bound]", Charge_Quantity = 3, Incubation_Speed = 7500,
    };
    
    private static readonly PetInfo BabyBloodyMurderer = new()
    {
        Pet_ID = 10018, NPC_ID = 83000019, Pet_Egg_ID = 22402139, Hatched_Egg_ID = 22402140, Incubation_Time = 12600,
    };
    
    private static readonly PetInfo MayaPurple = new()
    {
        Pet_ID = 10016, NPC_ID = 83000017, Pet_Egg_ID = 22402135, Hatched_Egg_ID = 22402136, Incubation_Time = 57600,
    };

    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EmptyList_IsRetailsEmptyForm()
    {
        
        Assert.Equal("01000000",
            Convert.ToHexString(PetIncubatorPackets.BuildAnsPCollectionIncubator([], Now)).ToLowerInvariant());
    }

    [Fact]
    public void TwoRecords_CarryTheFieldsTheClientReads_AtRetailsOffsets()
    {
        
        
        var retail = Convert.FromHexString(
            "010237d455015bd45501000147ec3b76b275e64000000000f67f00009d4191cbb475e64000000000f67f000000aab56b0c216dc0000000007e340300"
            + "37d455015bd455010101a1bd8476b275e64000000000f67f0000f712dacbb475e64000000000f67f00000050e4220c216dc0000000007e340300"
            + "0000");
        double SpanOf(int record) => BinaryPrimitives.ReadDoubleLittleEndian(retail.AsSpan(2 + PetIncubatorPackets.RecordLength * record + 42));
        
        
        var end0 = Now.AddDays(SpanOf(0));
        var end1 = Now.AddDays(SpanOf(1));
        var slots = new List<PetIncubatorSlot>
        {
            new() { Slot = 1, IncubatorId = 22402103, EggId = 22402139, Charges = 1, StartUtc = end1.AddSeconds(-6300), EndUtc = end1 },
            new() { Slot = 0, IncubatorId = 22402103, EggId = 22402139, Charges = 1, StartUtc = end0.AddSeconds(-6300), EndUtc = end0 },
        };
        var ours = PetIncubatorPackets.BuildAnsPCollectionIncubator(slots, Now);

        Assert.Equal(retail.Length, ours.Length);
        Assert.Equal(retail[..2], ours[..2]);                      
        Assert.Equal(retail[^2..], ours[^2..]);                    
        for (var r = 0; r < 2; r++)
        {
            int at = 2 + PetIncubatorPackets.RecordLength * r;
            Assert.Equal(retail.AsSpan(at, 10).ToArray(), ours.AsSpan(at, 10).ToArray()); 
            Assert.Equal(r, ours[at + 8]);                                                 
            Assert.Equal(SpanOf(r), BinaryPrimitives.ReadDoubleLittleEndian(ours.AsSpan(at + 42)), 7); 
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(ours.AsSpan(at + 50)));            
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(retail.AsSpan(at + 50)));
            
            double retailDur = BinaryPrimitives.ReadDoubleLittleEndian(retail.AsSpan(at + 26))
                - BinaryPrimitives.ReadDoubleLittleEndian(retail.AsSpan(at + 10));
            double ourDur = BinaryPrimitives.ReadDoubleLittleEndian(ours.AsSpan(at + 26))
                - BinaryPrimitives.ReadDoubleLittleEndian(ours.AsSpan(at + 10));
            Assert.Equal(6300, retailDur * 86400, 1);
            Assert.Equal(6300, ourDur * 86400, 1);
        }
    }

    
    private static bool Place(List<PetIncubatorSlot> slots, byte slot, IncubatorInfo incubator, DateTime now)
        => PetIncubatorRules.TryPlace(slots, slot, incubator.Incubator_ID, incubator.Charge_Quantity, now);

    private static bool InsertEgg(List<PetIncubatorSlot> slots, byte slot, uint incubatorId, IncubatorInfo incubator,
        PetInfo pet, DateTime now)
        => incubator.Incubator_ID == incubatorId
           && PetIncubatorRules.TryStartIncubation(slots, slot, incubatorId, pet.Pet_Egg_ID, now,
               now.AddSeconds(PetIncubatorStore.IncubationSeconds(incubator, pet)));

    [Fact]
    public void Place_StoresAFreshIncubator_OnlyInAFreeSlot()
    {
        var slots = new List<PetIncubatorSlot>();
        Assert.True(Place(slots, 0, Basic, Now));
        var s = PetIncubatorRules.SlotOf(slots, 0)!;
        Assert.Equal((22402101u, (byte)2, 0u), (s.IncubatorId, s.Charges, s.EggId));   
        Assert.Equal(Now, s.StartUtc);
        Assert.Equal(Now, s.EndUtc);

        Assert.False(Place(slots, 0, Cracked, Now));   
        Assert.False(Place(slots, 2, Cracked, Now));   
        Assert.True(Place(slots, 1, Cracked, Now));
        Assert.Equal(2, slots.Count);
    }

    [Fact]
    public void Egg_HatchTime_IsSpeedTimesTimeOver10000()
    {
        Assert.Equal(6300u, PetIncubatorStore.IncubationSeconds(Cracked, BabyBloodyMurderer));
        Assert.Equal(43200u, PetIncubatorStore.IncubationSeconds(GoldenBound, MayaPurple));
        Assert.Equal(12600u, PetIncubatorStore.IncubationSeconds(Basic, BabyBloodyMurderer));

        var slots = new List<PetIncubatorSlot>();
        Assert.False(InsertEgg(slots, 0, 22402103, Cracked, BabyBloodyMurderer, Now)); 
        Place(slots, 0, Cracked, Now.AddDays(-1));
        Assert.False(InsertEgg(slots, 0, 22402101, Basic, BabyBloodyMurderer, Now));    
        Assert.True(InsertEgg(slots, 0, 22402103, Cracked, BabyBloodyMurderer, Now));
        var s = PetIncubatorRules.SlotOf(slots, 0)!;
        Assert.Equal(22402139u, s.EggId);
        Assert.Equal((byte)1, s.Charges);                                  
        Assert.Equal(Now, s.StartUtc);
        Assert.Equal(Now.AddSeconds(6300), s.EndUtc);
        Assert.False(InsertEgg(slots, 0, 22402103, Cracked, BabyBloodyMurderer, Now)); 
    }

    [Fact]
    public void Collect_OnlyOnceHatched_TakesACharge_AndFreesAnEmptiedIncubator()
    {
        var slots = new List<PetIncubatorSlot>();
        Place(slots, 0, Basic, Now);
        Place(slots, 1, Cracked, Now);
        InsertEgg(slots, 0, 22402101, Basic, BabyBloodyMurderer, Now);
        InsertEgg(slots, 1, 22402103, Cracked, BabyBloodyMurderer, Now);

        var early = Now.AddSeconds(6299);
        Assert.Null(PetIncubatorRules.CollectableSlot(slots, 1, 22402103, early));          
        var later = Now.AddSeconds(12600);
        Assert.Null(PetIncubatorRules.CollectableSlot(slots, 1, 22402101, later));          

        var basic = PetIncubatorRules.CollectableSlot(slots, 0, 22402101, later)!;
        PetIncubatorRules.ApplyCollect(slots, basic, later);
        var left = PetIncubatorRules.SlotOf(slots, 0)!;
        Assert.Equal((0u, (byte)1), (left.EggId, left.Charges));   
        Assert.Equal(later, left.StartUtc);
        Assert.Equal(later, left.EndUtc);
        Assert.Null(PetIncubatorRules.CollectableSlot(slots, 0, 22402101, later));          

        var cracked = PetIncubatorRules.CollectableSlot(slots, 1, 22402103, later)!;
        PetIncubatorRules.ApplyCollect(slots, cracked, later);
        Assert.Null(PetIncubatorRules.SlotOf(slots, 1));                                     
    }

    [Fact]
    public void BagCell_MapsLikeTheItemRecord()
    {
        Assert.Equal(0, PetIncubatorStore.BagSlotToFlat(1, 50));
        Assert.Equal(19, PetIncubatorStore.BagSlotToFlat(1, 69));
        Assert.Equal(-1, PetIncubatorStore.BagSlotToFlat(1, 70));
        Assert.Equal(20 + 35 + 3, PetIncubatorStore.BagSlotToFlat(3, 3));
        Assert.Equal(-1, PetIncubatorStore.BagSlotToFlat(5, 0));
    }
}
