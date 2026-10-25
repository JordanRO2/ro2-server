using System.Buffers.Binary;
using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gate for RMI 2506 OnNPC_In, the batched NPC spawn message.
/// </summary>
/// <remarks>
/// The envelope is a compact-length marker whose declared length equals the blob length, a fixed msgTag at
/// blob+2 and a zero u32 at blob+4. Record fields were located against the shipped spawn data keyed on asset index,
/// so position, rotation and level are predictions rather than fitted parameters. The fixture record is matched to
/// its spawn row, so the test compares the builder against the wire format rather than against itself.
/// Spawns are sent as one enveloped message per batch; a bare per-NPC body is malformed and crashes the client.
/// The payload length is count * 83 in most frames. The builder emits the common shape, so the divisibility
/// assertion checks this server's output, not a property of the opcode.
/// </remarks>
public class OnNpcIn2506Tests
{
    
    private static readonly byte[] RetailRecord = Convert.FromHexString(
        "135d2000a018b40114d0ae00006e7d0000a5010000d0ae00006e7d0000a501000001000096" +
        "43000000003c0500003c0500007b0600007b06000000000000000000000000000000000100000000000000000000");

    
    private static NpcRuntimeState SampleNpc() => new()
    {
        EntityOid = 0x00205D13u,          
        NpcId = 28580000u,
        MapId = 1300u,
        Level = 20,
        Hp = 954,
        MaxHp = 954,
        Camp = 1,
        
        
        
        NavCell = 421,
        
        PosX = 447.525635f,
        PosY = 321.107849f,
        Spawn = new SpawnEntry
        {
            MapId = 1300u,
            Index = 383,
            AssetIndex = 28580000u,
            PosX = 447.525635f,
            PosY = 321.107849f,
            PosZ = 12.481079f,   
            Rot = 300.0f,
        },
    };

    private static (byte[] Blob, int PrefixLen) Build(params NpcRuntimeState[] npcs)
    {
        var body = NpcSpawnService.BuildOnNpcIn2506(npcs);
        var width = body[0];
        return (body, 1 + width);
    }

    [Fact]
    public void Envelope_MatchesRetailsInvariants()
    {
        var (body, prefix) = Build(SampleNpc());
        var blob = body.AsSpan(prefix);

        
        Assert.Equal(blob.Length, body[1]);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(0xE3D0, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
        Assert.Equal(NpcSpawnService.MsgTagOnNpcIn,
            BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));   
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(8)));    
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(40)]
    public void OurPayloadAlwaysDividesBy83(int count)
    {
        
        
        
        var npcs = Enumerable.Range(0, count).Select(_ => SampleNpc()).ToArray();
        var (body, prefix) = Build(npcs);
        var payload = body.Length - prefix - 10;   
        Assert.Equal(0, payload % NpcSpawnService.NpcRecordLen);
        Assert.Equal(count, payload / NpcSpawnService.NpcRecordLen);
        Assert.Equal(count, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(prefix + 8)));
    }

    [Fact]
    public void Record_Is71Of83BytesIdenticalToTheRetailRecord()
    {
        var (body, prefix) = Build(SampleNpc());
        var rec = body.AsSpan(prefix + 10, NpcSpawnService.NpcRecordLen).ToArray();
        Assert.Equal(NpcSpawnService.NpcRecordLen, rec.Length);

        var differing = Enumerable.Range(0, NpcSpawnService.NpcRecordLen)
            .Where(i => rec[i] != RetailRecord[i]).ToArray();
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        Assert.Equal(new[] { 42, 43, 46, 47, 50, 51, 54, 55 }, differing);
        Assert.Equal(75, NpcSpawnService.NpcRecordLen - differing.Length);
    }

    [Fact]
    public void NavCell_LandsAtBothPositionSlots()
    {
        var (body, prefix) = Build(SampleNpc());
        var rec = body.AsSpan(prefix + 10);
        Assert.Equal(421, BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(17)));
        Assert.Equal(421, BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(29)));
    }

    [Fact]
    public void NavCell_IsNeverOutsideRetailsObservedRange()
    {
        
        
        
        
        
        var absurd = SampleNpc();
        absurd.NavCell = 8711;                    
        var (body, prefix) = Build(absurd);
        var cell = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(prefix + 10 + 17));
        Assert.InRange(cell, 0, GameDataStore.MaxKnownNavCell);

        
        
        var unknown = SampleNpc();
        unknown.NavCell = null;
        var (body2, prefix2) = Build(unknown);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body2.AsSpan(prefix2 + 10 + 17)));

        Assert.Equal(0, NpcSpawnService.ToWireNavCell(null));
        Assert.Equal(0, NpcSpawnService.ToWireNavCell(-1));
        Assert.Equal(0, NpcSpawnService.ToWireNavCell(GameDataStore.MaxKnownNavCell + 1));
        Assert.Equal(2382, NpcSpawnService.ToWireNavCell(2382));
    }

    [Fact]
    public void EveryRealMap1100SpawnEmitsAnInRangeCell()
    {
        
        
        var npcs = Enumerable.Range(0, 200)
            .Select(i =>
            {
                var n = SampleNpc();
                
                n.NavCell = i % 3 == 0 ? null : (i % 3 == 1 ? i * 7 : i * 500);
                return n;
            })
            .ToArray();
        var (body, prefix) = Build(npcs);
        for (var i = 0; i < npcs.Length; i++)
        {
            var rec = body.AsSpan(prefix + 10 + i * NpcSpawnService.NpcRecordLen);
            Assert.InRange(BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(17)),
                0, GameDataStore.MaxKnownNavCell);
            Assert.InRange(BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(29)),
                0, GameDataStore.MaxKnownNavCell);
        }
    }

    [Fact]
    public void HandleCarriesTheNpcTypeTag()
    {
        var (body, prefix) = Build(SampleNpc());
        var rec = body.AsSpan(prefix + 10);
        
        
        Assert.Equal(NpcStateManager.NpcActorTag, rec[0]);
        Assert.Equal(0x00205D13u, BinaryPrimitives.ReadUInt32LittleEndian(rec));
    }

    [Fact]
    public void PositionIsTruncatedHundredths_AndTheOriginDestinationPairIsIdentical()
    {
        var (body, prefix) = Build(SampleNpc());
        var rec = body.AsSpan(prefix + 10);

        
        
        Assert.Equal(44752, BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(9)));
        Assert.Equal(32110, BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(13)));
        Assert.Equal(44753, (int)Math.Round(447.525635f * 100.0f));   
        Assert.Equal(44752, NpcSpawnService.ToWireCoord(447.525635f));

        
        Assert.Equal(rec.Slice(9, 12).ToArray(), rec.Slice(21, 12).ToArray());
    }

    [Fact]
    public void RotationIsAFloatAt34_AndLevelAByteAt8()
    {
        var (body, prefix) = Build(SampleNpc());
        var rec = body.AsSpan(prefix + 10);
        
        
        Assert.Equal(300.0f, BinaryPrimitives.ReadSingleLittleEndian(rec.Slice(34)));
        
        
        Assert.Equal(20, rec[8]);
    }

    [Fact]
    public void ModalAndConstantTailMatchesRetail()
    {
        var (body, prefix) = Build(SampleNpc());
        var rec = body.AsSpan(prefix + 10);
        
        
        
        
        Assert.Equal(0x01, rec[33]);
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(rec.Slice(38)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(58)));
        for (int i = 62; i <= 71; i++) Assert.Equal(0, rec[i]);
        Assert.Equal(0x01, rec[72]);                       
        for (int i = 73; i <= 82; i++) Assert.Equal(0, rec[i]);
    }

    [Fact]
    public void HostileAndPeacefulNpcsCarryDifferentConstants()
    {
        
        
        
        
        
        
        
        
        var monster = SampleNpc();
        monster.Camp = 2;
        var (mb, mp) = Build(monster);
        Assert.Equal(3, mb.AsSpan(mp + 10)[72]);

        var friendly = SampleNpc();
        friendly.Camp = 1;
        var (fb, fp) = Build(friendly);
        Assert.Equal(1, fb.AsSpan(fp + 10)[72]);

        
        
        
        const float walk = 3.5f;
        var mrec = NpcSpawnService.BuildOnNpcIn2506(new[] { monster }, _ => walk);
        var frec = NpcSpawnService.BuildOnNpcIn2506(new[] { friendly }, _ => walk);
        Assert.Equal(walk, BinaryPrimitives.ReadSingleLittleEndian(mrec.AsSpan(1 + mrec[0] + 10 + 38)));
        Assert.Equal(walk, BinaryPrimitives.ReadSingleLittleEndian(frec.AsSpan(1 + frec[0] + 10 + 38)));
    }
}
