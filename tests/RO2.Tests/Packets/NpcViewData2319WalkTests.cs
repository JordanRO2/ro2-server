using System.Buffers.Binary;
using RO2.Domain.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Walk state of RMI 2319 OnNPCViewData. The record describes the NPC's current walk (NpcRuntimeState.LastMove),
/// not a standing actor at the end of the walk: a mid-walk record carries the destination of the last 2318 and
/// both cells and the speed, while a standing record carries a flag of 1.
/// </summary>
public class NpcViewData2319WalkTests
{
    
    
    
    
    
    private static readonly byte[] RetailMidWalk2319 = Convert.FromHexString(
        "015b5b0098e200000000" +
        "133ba802503db102017a5300004e5600001c0000006c530000735500001c00000002000000000000803f" +
        "33000000330000002a0000002a00000000000000000000000100000000000300000000000000000000");

    private static readonly DateTime MoveA = new(2026, 8, 9, 11, 22, 13, 868, DateTimeKind.Utc);

 /// <summary>The NPC after the server has sent its 2318: position already at the destination, with the walk on LastMove.</summary>
    private static NpcRuntimeState DonorA() => new()
    {
        EntityOid = 0x02A83B13u,
        NpcId = 45170000u,
        MapId = 1300u,
        Level = 1,
        Hp = 51,
        MaxHp = 51,
        Camp = 2,
        NavCell = 28,
        PosX = 21356 / 100f,
        PosY = 21875 / 100f,
        Spawn = new SpawnEntry { MapId = 1300u, Index = 1, AssetIndex = 45170000u, Rot = 0f },
        LastMove = new NpcMove(21373 / 100f, 22124 / 100f, 21356 / 100f, 21875 / 100f, MoveA, 1.0f),
    };

    [Fact]
    public void MidWalk_AtOneRetailStep_ReproducesTheRetailRecord_ButForSpAndOffset66()
    {
        
        
        
        var body = NpcSpawnService.BuildOnNpcViewData2319(DonorA(), walkSpeed: 1.0f, nowUtc: MoveA.AddMilliseconds(300));

        Assert.Equal(RetailMidWalk2319.Length, body.Length);
        Assert.Equal(RetailMidWalk2319[..10], body[..10]);
        var rec = body[10..];
        var retail = RetailMidWalk2319[10..];
        var differing = Enumerable.Range(0, NpcSpawnService.NpcRecordLen).Where(i => rec[i] != retail[i]).ToArray();

        
        
        
        Assert.Equal(new[] { 50, 54, 66 }, differing);
        Assert.Equal(NpcSpawnService.StateWalking, rec[33]);
    }

    [Fact]
    public void MidWalk_AtTheCapturesOwnElapsed_StaysOnTheSegmentWithinARetailTick()
    {
        
        
        var body = NpcSpawnService.BuildOnNpcViewData2319(DonorA(), walkSpeed: 1.0f, nowUtc: MoveA.AddMilliseconds(313));
        var rec = body[10..];
        var retail = RetailMidWalk2319[10..];

        for (var off = 9; off <= 13; off += 4)
        {
            var ours = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(off));
            var theirs = BinaryPrimitives.ReadInt32LittleEndian(retail.AsSpan(off));
            Assert.InRange(Math.Abs(ours - theirs), 0, 3);   
        }
        Assert.Equal(retail[17..50], rec[17..50]);
    }

    
    
    
    
    
    private static readonly byte[] RetailArrived2319 = Convert.FromHexString(
        "015b5b0098e20000000013a74901bef8fa0232b5f40100c8cd0100cb050000b5f40100c8cd0100cb050000" +
        "01000000000000803f567e0000567e0000030f0000030f000000000000000000000000000000000300000000000000000000");

    [Fact]
    public void CoveredWalk_StandsAtTheDestination_ByteExactButForSp()
    {
        var start = new DateTime(2026, 7, 21, 8, 37, 38, 569, DateTimeKind.Utc);
        var npc = new NpcRuntimeState
        {
            EntityOid = 0x0149A713u,
            NpcId = 50002110u,
            MapId = 1u,
            Level = 50,
            Hp = 32342,
            MaxHp = 32342,
            Camp = 2,
            NavCell = 1483,
            PosX = 128181 / 100f,
            PosY = 118216 / 100f,
            Spawn = new SpawnEntry { MapId = 1u, Index = 1, AssetIndex = 50002110u, Rot = 0f },
            LastMove = new NpcMove(128165 / 100f, 118203 / 100f, 128181 / 100f, 118216 / 100f, start, 1.0f),
        };

        var body = NpcSpawnService.BuildOnNpcViewData2319(npc, walkSpeed: 1.0f, nowUtc: start.AddMilliseconds(364));

        var rec = body[10..];
        var differing = Enumerable.Range(0, NpcSpawnService.NpcRecordLen)
            .Where(i => rec[i] != RetailArrived2319[10 + i]).ToArray();
        
        Assert.Equal(new[] { 50, 51, 54, 55 }, differing);
        Assert.Equal(NpcSpawnService.StateStanding, rec[33]);
    }

    
    
    
    
    
    private static readonly byte[] RetailAfterStop2319 = Convert.FromHexString(
        "015b5b0098e20000000013d358025065da0150574c0100794801007c000000574c0100794801007c000000" +
        "0100000000000000004286090042860900b52c0000b52c00000000000000000000000001000000ef03000000000000000000");

    [Fact]
    public void AfterAWalk_Offset38_IsTheWalksSpeed_NotTheTableWalkSpeed()
    {
        var start = new DateTime(2026, 7, 21, 2, 5, 22, 759, DateTimeKind.Utc);
        var npc = new NpcRuntimeState
        {
            EntityOid = 0x0258D313u,
            NpcId = 31090000u,
            MapId = 1u,
            Level = 80,
            Hp = 624194,
            MaxHp = 624194,
            Camp = 1,
            NavCell = 124,
            PosX = 85079 / 100f,
            PosY = 84089 / 100f,
            Spawn = new SpawnEntry { MapId = 1u, Index = 1, AssetIndex = 31090000u, Rot = 0f },
            LastMove = new NpcMove(85079 / 100f, 84089 / 100f, 85079 / 100f, 84089 / 100f, start, 0f),
        };

        var body = NpcSpawnService.BuildOnNpcViewData2319(npc, walkSpeed: 1.0f, nowUtc: start.AddMilliseconds(306));

        var rec = body[10..];
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(rec.AsSpan(38)));
        var differing = Enumerable.Range(0, NpcSpawnService.NpcRecordLen)
            .Where(i => rec[i] != RetailAfterStop2319[10 + i]).ToArray();
        
        
        
        Assert.Equal(new[] { 50, 51, 54, 55, 68, 72, 73 }, differing);
    }

    
    [Fact]
    public void NoWalkSent_IsTodaysStandingRecord_WithTheTableWalkSpeed()
    {
        var npc = DonorA();
        npc.LastMove = null;

        var rec = NpcSpawnService.BuildOnNpcViewData2319(npc, walkSpeed: 2.5f, nowUtc: MoveA)[10..];

        Assert.Equal(NpcSpawnService.StateStanding, rec[33]);
        Assert.Equal(rec[9..17], rec[21..29]);
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(rec.AsSpan(38)));
    }

    [Fact]
    public void ADeadNpc_IsNeverReportedMidWalk()
    {
        var npc = DonorA();
        npc.IsDead = true;

        var rec = NpcSpawnService.BuildOnNpcViewData2319(npc, walkSpeed: 1.0f, nowUtc: MoveA.AddMilliseconds(300))[10..];

        Assert.Equal(NpcSpawnService.StateStanding, rec[33]);
        Assert.Equal(21356, BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(9)));
        Assert.Equal(21875, BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(13)));
    }

    [Fact]
    public void The2506Record_StillStandsAtTheLivePosition_WhateverTheWalk()
    {
        
        var npc = DonorA();
        var batched = NpcSpawnService.BuildOnNpcIn2506(new[] { npc }, _ => 1.0f);
        var rec = batched[(1 + batched[0] + 10)..];

        Assert.Equal(rec[9..17], rec[21..29]);
        Assert.Equal(21356, BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(9)));
        Assert.Equal(0x01, rec[33]);
    }

    [Theory]
    [InlineData(0, true, 21373, 22124)]       
    [InlineData(300, true, 21370, 22094)]     
    [InlineData(2495, true, 21356, 21875)]    
    [InlineData(2600, false, 21356, 21875)]   
    [InlineData(-500, true, 21373, 22124)]    
    public void NpcMove_InterpolatesAlongTheSegment(int elapsedMs, bool inProgress, int wireX, int wireY)
    {
        var walk = DonorA().LastMove!;
        var now = MoveA.AddMilliseconds(elapsedMs);

        Assert.Equal(inProgress, walk.InProgress(now));
        var (x, y) = walk.Position(now);
        Assert.InRange(Math.Abs(NpcSpawnService.ToWireCoord(x) - wireX), 0, 1);
        Assert.InRange(Math.Abs(NpcSpawnService.ToWireCoord(y) - wireY), 0, 1);
    }

    [Fact]
    public void NpcMove_WithNoLengthOrNoSpeed_IsNeverInProgress_AndSitsAtItsEnd()
    {
        var stop = new NpcMove(1f, 2f, 1f, 2f, MoveA, 1f);
        var frozen = new NpcMove(1f, 2f, 5f, 2f, MoveA, 0f);

        Assert.False(stop.InProgress(MoveA));
        Assert.False(frozen.InProgress(MoveA.AddSeconds(1)));
        Assert.Equal((5f, 2f), frozen.Position(MoveA.AddSeconds(1)));
    }
}
