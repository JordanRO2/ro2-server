using System.Buffers.Binary;
using RO2.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout test for RMI 1306 ReqActorViewData and its reply, RMI 2319 OnNPCViewData.
/// The target handle is at body+10. body+4 holds the constant msgTag, so reading the handle there never matches.
/// Requests always target an NPC (tag 0x13), so the lookup must use the NPC manager, not PlayerManager.
/// The reply carries the requested handle and is sent only in answer to a 1306.
/// </summary>
public class ActorViewData1306Tests
{
    
    private static readonly byte[] RetailRequest =
        Convert.FromHexString("010c0c0096e20000000013bb7d01");

    
    
    private static readonly byte[] OurClientRequest =
        Convert.FromHexString("010c0c0096e20000000013200200");

    
    
    private static readonly byte[] Retail2319Body = Convert.FromHexString(
        "015b5b0098e200000000" +
        "13eac602503db10201345600006358000019000000f55500000a570000190000000200000000" +
        "0000803f33000000330000002a0000002a00000000000000000000000100000000000300000000000000000000");

    private static readonly byte[] Retail2319Record = Retail2319Body[10..];

 /// <summary>
 /// The actor described by the reply above, rebuilt from its own fields: a level-1 monster (asset 45170000)
 /// with 51 HP, standing on navmesh cell 25.
 /// </summary>
    private static NpcRuntimeState DonorNpc() => new()
    {
        EntityOid = 0x02C6EA13u,   
        NpcId = 45170000u,
        MapId = 1300u,
        Level = 1,
        Hp = 51,
        MaxHp = 51,
        Camp = 2,                  
        NavCell = 25,
        
        PosX = 220.68f,
        PosY = 226.27f,
        Spawn = new SpawnEntry
        {
            MapId = 1300u,
            Index = 1,
            AssetIndex = 45170000u,
            PosX = 220.68f,        
            PosY = 226.27f,        
            PosZ = 0f,             
            Rot = 0f,
        },
    };

 /// <summary>
 /// record+38 is the asset's Walk_Speed from the NPC data table (1.0 for asset 45170000). It is not a hostility flag.
 /// </summary>
    private const float DonorWalkSpeed = 1.0f;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Request_HandleIsAtOffset10_AndOffset4IsTheMsgTag(bool retail)
    {
        var body = retail ? RetailRequest : OurClientRequest;
        var expected = retail ? 0x017DBB13u : 0x00022013u;

        Assert.Equal(ActorViewDataHandler.BodyLength, body.Length);
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });           
        Assert.Equal(body.Length - 2, body[1]);                        
        Assert.Equal(body.Length - 2, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)));

        
        Assert.Equal(58006, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        Assert.Equal(58006u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)));

        var handle = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(ActorViewDataHandler.HandleOffset, 4));
        Assert.Equal(expected, handle);
        Assert.Equal(0x13u, handle & 0xFFu);   
    }

    [Fact]
    public void Reply2319_EnvelopeAndHeaderAreByteIdenticalToRetail()
    {
        var body = NpcSpawnService.BuildOnNpcViewData2319(DonorNpc(), DonorWalkSpeed);

        Assert.Equal(Retail2319Body.Length, body.Length);            
        Assert.Equal(Retail2319Body[..10], body[..10]);              

        var blob = body.AsSpan(1 + body[0]);
        Assert.Equal(blob.Length, body[1]);                          
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));   
        Assert.Equal(58008, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
        Assert.Equal(NpcSpawnService.MsgTagOnNpcViewData,
            BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));   
        Assert.Equal(8 + NpcSpawnService.NpcRecordLen, blob.Length);
    }

    [Fact]
    public void Reply2319_RecordDeviationsFromRetailArePinned()
    {
        var body = NpcSpawnService.BuildOnNpcViewData2319(DonorNpc(), DonorWalkSpeed);
        var rec = body[10..];
        Assert.Equal(NpcSpawnService.NpcRecordLen, rec.Length);

        var differing = Enumerable.Range(0, NpcSpawnService.NpcRecordLen)
            .Where(i => rec[i] != Retail2319Record[i]).ToArray();

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        Assert.Equal(new[] { 21, 22, 25, 26, 33, 50, 54, 66 }, differing);
    }

    [Fact]
    public void Reply2319_CarriesTheSameRecordAs2506()
    {
        
        
        
        var npc = DonorNpc();
        var from2319 = NpcSpawnService.BuildOnNpcViewData2319(npc, DonorWalkSpeed)[10..];
        var batched = NpcSpawnService.BuildOnNpcIn2506(new[] { npc }, _ => DonorWalkSpeed);
        var from2506 = batched[(1 + batched[0] + 10)..];

        Assert.Equal(NpcSpawnService.NpcRecordLen, from2319.Length);
        Assert.Equal(NpcSpawnService.NpcRecordLen, from2506.Length);
        Assert.Equal(from2506, from2319);
    }

    [Fact]
    public void Reply2319_And2506_AgreeOnAWanderedNpc_AtItsLivePosition()
    {
        
        
        
        
        
        var npc = DonorNpc();
        npc.PosX = 223.5f;
        npc.PosY = 224.0f;

        var from2319 = NpcSpawnService.BuildOnNpcViewData2319(npc, DonorWalkSpeed)[10..];
        var batched = NpcSpawnService.BuildOnNpcIn2506(new[] { npc }, _ => DonorWalkSpeed);
        var from2506 = batched[(1 + batched[0] + 10)..];

        Assert.Equal(from2506, from2319);
        foreach (var rec in new[] { from2319, from2506 })
        {
            
            Assert.Equal(22350, BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(9)));
            Assert.Equal(22400, BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(13)));
            Assert.Equal(22350, BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(21)));
            Assert.Equal(22400, BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(25)));
        }
    }

    [Fact]
    public void Reply2319_EnvelopeMarkerIsAlwaysAValidCompactWidth()
    {
        
        
        
        foreach (var hp in new[] { 1, 51, 954, 65535, 1_000_000 })
        {
            var npc = DonorNpc();
            npc.Hp = hp;
            npc.MaxHp = hp;
            var body = NpcSpawnService.BuildOnNpcViewData2319(npc, DonorWalkSpeed);
            Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
            Assert.Equal(body.Length - (1 + body[0]), BinaryPrimitives.ReadUInt16LittleEndian(
                body.AsSpan(1 + body[0])));
        }
    }
}
