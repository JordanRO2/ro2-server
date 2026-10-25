using System.Buffers.Binary;
using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gates for the NPC life-cycle notifies 2317 OnRegenNPC and 2507 OnNPC_Out.
/// </summary>
/// <remarks>
/// Both opcodes open with a compact-length marker in {1,2,4,8} whose declared length equals the blob length.
/// 2507 is a map broadcast, so a malformed frame reaches everybody in range at once.
/// </remarks>
public class NpclifePacketsTests
{
    private static ReadOnlySpan<byte> Blob(byte[] body) => body.AsSpan(1 + body[0]);

 /// <summary>
 /// The envelope invariant shared by the enveloped opcodes of this family: a compact-length marker in {1,2,4,8},
 /// a declared length equal to the blob length, the same value repeated as the u16 at blob+0, the opcode's msgTag
 /// at blob+2, and a zero u32 at blob+4.
 /// </summary>
    private static void AssertWellFormed(byte[] body, ushort tag)
    {
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });

        var prefix = 1 + body[0];
        var declared = 0L;
        for (var i = body[0] - 1; i >= 0; i--)
            declared = (declared << 8) | body[1 + i];

        var blob = body.AsSpan(prefix);
        Assert.Equal(blob.Length, (int)declared);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(tag, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
    }

    
    
    
 /// <summary>
 /// A 2317 body for a level 80 NPC: handle, asset, position, navmesh cell, rotation, walk speed, HP/SP and camp byte.
 /// </summary>
    private const string RetailRegenNpcFrame =
        "015b5b009ae20000000013f89d018cfdfa0250d2ff00009a88000061000000d2ff00009a88000061000000" +
        "00000034439a99d93f18d6010018d60100b52c0000b52c0000000000000000000000000000000003000000" +
        "00000000000000";

 /// <summary>The NPC that the fixture body describes, as this server would hold it.</summary>
    private static NpcRuntimeState RetailRegenNpc() => new()
    {
        EntityOid = 0x019DF813u,
        NpcId = 50003340u,
        MapId = 1300u,
        Level = 80,
        Hp = 120344,
        MaxHp = 120344,
        Camp = 2,               
        NavCell = 97,
        PosX = 654.90f,
        PosY = 349.70f,
        Spawn = new SpawnEntry
        {
            MapId = 1300u,
            Index = 1,
            AssetIndex = 50003340u,
            PosX = 654.90f,
            PosY = 349.70f,
            PosZ = 0f,          
            Rot = 180.0f
        }
    };

 /// <summary>
 /// Byte-exact gate for the 2317 body, except the four bytes that carry SP. The server has no SP model for NPCs
 /// and mirrors HP into those slots. The differing offsets are enumerated rather than asserted equal, so a future
 /// change cannot silently widen the gap.
 /// </summary>
    [Fact]
    public void RegenNpc2317_ReproducesARealRetailFrameExceptTheUnmodelledSpSlots()
    {
        var expected = Convert.FromHexString(RetailRegenNpcFrame);
        var body = NpcLifecyclePackets.BuildRegenNpc2317(RetailRegenNpc(), walkSpeed: 1.7f);

        Assert.Equal(NpcLifecyclePackets.RegenNpcBodyLen, body.Length);
        Assert.Equal(expected.Length, body.Length);

        var differing = Enumerable.Range(0, body.Length).Where(i => body[i] != expected[i]).ToArray();
        
        
        Assert.Equal(new[] { 60, 61, 64, 65 }, differing);

        
        
        var rec = Blob(body).Slice(8);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(52)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(56)));
    }

    [Fact]
    public void RegenNpc2317_EnvelopeAndRecordFramingMatchTheClientsReader()
    {
        var body = NpcLifecyclePackets.BuildRegenNpc2317(RetailRegenNpc(), 1.7f);

        AssertWellFormed(body, NpcLifecyclePackets.MsgTagRegenNpc);

        var blob = Blob(body);
        
        
        Assert.Equal(8 + NpcLifecyclePackets.NpcRecordFixedLen + 2, blob.Length);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(
            blob.Slice(8 + NpcLifecyclePackets.NpcRecordFixedLen)));
    }

    [Fact]
    public void RegenNpc2317_RecordCarriesTheNpcTagAndTheMeasuredConstants()
    {
        var body = NpcLifecyclePackets.BuildRegenNpc2317(RetailRegenNpc(), 1.7f);
        var rec = Blob(body).Slice(8);

        
        Assert.Equal(0x13u, BinaryPrimitives.ReadUInt32LittleEndian(rec) & 0xFFu);
        Assert.Equal(50003340u, BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(4)));
        Assert.Equal(80, rec[8]);

        
        
        Assert.Equal(65490, BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(9)));
        Assert.Equal(34970, BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(13)));
        Assert.Equal(97, BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(17)));

        
        
        
        Assert.Equal(0, rec[33]);

        
        
        Assert.Equal(1.7f, BinaryPrimitives.ReadSingleLittleEndian(rec.Slice(38)));

        
        
        Assert.True(rec.Slice(9, 12).SequenceEqual(rec.Slice(21, 12)));

        Assert.Equal(3, rec[72]);
    }

 /// <summary>
 /// ZoneSpawn's rotation column reaches 2400 degrees. The client divides the field by 180/pi, so wrapping into
 /// [0,360) preserves the value. Staying inside the observed range is what matters: values outside it have
 /// crashed the client before.
 /// </summary>
    [Theory]
    [InlineData(2400f, 240f)]
    [InlineData(-140f, 220f)]
    [InlineData(180f, 180f)]
    [InlineData(0f, 0f)]
    [InlineData(float.NaN, 0f)]
    public void RegenNpc2317_NormalisesSpawnTableRotationsIntoRetailsRange(float raw, float expected)
    {
        Assert.Equal(expected, NpcSpawnService.ToWireRotationDegrees(raw), 3);

        var npc = RetailRegenNpc();
        npc.Spawn = npc.Spawn with { Rot = raw };
        var rec = Blob(NpcLifecyclePackets.BuildRegenNpc2317(npc, 1.7f)).Slice(8);
        var onWire = BinaryPrimitives.ReadSingleLittleEndian(rec.Slice(34));
        Assert.True(onWire >= 0f && onWire < 360f, "rotation " + onWire + " is outside retail's range");
    }

    
    
    
    [Fact]
    public void NpcOut2507_ReproducesARealSingleHandleRetailFrame()
    {
        
        var expected = Convert.FromHexString("010e0e00d1e300000000010013430100");
        Assert.Equal(expected, NpcLifecyclePackets.BuildNpcOut2507(0x00014313u));
    }

    [Fact]
    public void NpcOut2507_ReproducesRealMultiHandleRetailFrames()
    {
        var three = Convert.FromHexString(
            "01161600d1e3000000000300133311001335110013691100");
        Assert.Equal(three, NpcLifecyclePackets.BuildNpcOut2507(
            new[] { 0x00113313u, 0x00113513u, 0x00116913u }));

        var five = Convert.FromHexString(
            "011e1e00d1e30000000005001360010013090100130b01001314010013150100");
        Assert.Equal(five, NpcLifecyclePackets.BuildNpcOut2507(
            new[] { 0x00016013u, 0x00010913u, 0x00010b13u, 0x00011413u, 0x00011513u }));
    }

    [Fact]
    public void NpcOut2507_LengthModelHoldsAcrossRetailsWholeCountRange()
    {
        
        
        
        foreach (var count in new[] { 1, 2, 3, 5, 31, 32, 29, 100, 170 })
        {
            var handles = new uint[count];
            for (var i = 0; i < count; i++)
                handles[i] = (uint)((i + 1) << 8) | 0x13u;

            var body = NpcLifecyclePackets.BuildNpcOut2507(handles);
            AssertWellFormed(body, NpcLifecyclePackets.MsgTagNpcOut);

            var blob = Blob(body);
            Assert.Equal(count, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(8)));
            Assert.Equal(10 + 4 * count, blob.Length);

            for (var i = 0; i < count; i++)
            {
                var h = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(10 + 4 * i));
                Assert.Equal(handles[i], h);
                
                Assert.Equal(0x13u, h & 0xFFu);
            }
        }
    }

    [Fact]
    public void NpcOut2507_CrossesTheCompactLengthMarkerBoundaryTheWayRetailDoes()
    {
        
        
        var narrow = NpcLifecyclePackets.BuildNpcOut2507(NpcHandles(29));
        Assert.Equal(1, narrow[0]);
        Assert.Equal(126, Blob(narrow).Length);

        var wide = NpcLifecyclePackets.BuildNpcOut2507(NpcHandles(30));
        Assert.Equal(2, wide[0]);
        Assert.Equal(130, Blob(wide).Length);
        AssertWellFormed(wide, NpcLifecyclePackets.MsgTagNpcOut);

        static uint[] NpcHandles(int n)
        {
            var h = new uint[n];
            for (var i = 0; i < n; i++) h[i] = (uint)((i + 1) << 8) | 0x13u;
            return h;
        }
    }

    [Fact]
    public void NpcOut2507_RefusesAnEmptyList()
    {
        
        
        Assert.Throws<ArgumentException>(() => NpcLifecyclePackets.BuildNpcOut2507(Array.Empty<uint>()));
    }

    
    
    
    
    
    
}
