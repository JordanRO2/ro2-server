using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the in-world 2498 re-push (TraitPackets.BuildFullPcTrait2498 with currentHp and currentSp).
/// The client applies blob+72 and blob+76 as the current HP and SP on every 2498, and the server re-sends it on each
/// map load and gear change, so the re-push must carry the live PlayerState values, not the login snapshot.
/// </summary>
public class PcTrait2498LiveVitalsTests
{
    private const int MaxHpBodyOffset = 11;   
    private const int MaxSpBodyOffset = 15;   
    private const int CurHpBodyOffset = 75;   
    private const int CurSpBodyOffset = 79;   

 /// <summary>A snapshot that logged in at full HP and SP; the three-argument form would send it at full.</summary>
    private static Character LoggedInAtFull() => new() { Level = 10, Hp = 1000, MaxHp = 1000, Mp = 100, MaxMp = 100 };

    private static (uint maxHp, ushort maxSp, uint curHp, ushort curSp) Vitals(byte[] body) => (
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(MaxHpBodyOffset)),
        BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(MaxSpBodyOffset)),
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(CurHpBodyOffset)),
        BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(CurSpBodyOffset)));

 /// <summary>
 /// Regression: a PC revived at half HP, or damaged since login, was reset to its login value by every re-push while
 /// the server kept the real value.
 /// </summary>
    [Fact]
    public void TheLiveCurrentVitalsAreSentNotTheSnapshots()
    {
        var body = TraitPackets.BuildFullPcTrait2498(LoggedInAtFull(), null, null, 500, 50);

        var (maxHp, maxSp, curHp, curSp) = Vitals(body);
        Assert.Equal(1000u, maxHp);
        Assert.Equal((ushort)100, maxSp);
        Assert.Equal(500u, curHp);
        Assert.Equal((ushort)50, curSp);
    }

 /// <summary>Current HP never exceeds max HP, for the live pair as well.</summary>
    [Fact]
    public void LiveVitalsAboveTheSheetMaxAreCutBack()
    {
        var body = TraitPackets.BuildFullPcTrait2498(LoggedInAtFull(), null, null, 80000, 9000);

        var (maxHp, maxSp, curHp, curSp) = Vitals(body);
        Assert.Equal(maxHp, curHp);
        Assert.Equal(maxSp, curSp);
    }

 /// <summary>
 /// A dead PC's sheet keeps 0. The snapshot form treats a stored 0 as fresh and sends full vitals, which is correct at
 /// world entry but wrong for a PC that is dead in the world.
 /// </summary>
    [Fact]
    public void ADeadPcStaysAtZero()
    {
        var body = TraitPackets.BuildFullPcTrait2498(LoggedInAtFull(), null, null, 0, 0);

        var (_, _, curHp, curSp) = Vitals(body);
        Assert.Equal(0u, curHp);
        Assert.Equal((ushort)0, curSp);
    }
}
