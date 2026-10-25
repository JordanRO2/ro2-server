using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// The monster-AI aggression slice: the pure acquisition decision (<see cref="NpcAggression.Acquires"/>, the
/// always / by-level / passive rule), the custom table's parsing, and the leash reset
/// (<see cref="NpcStateManager.LeashReset"/>: clear threat + heal to full). Pure: no sockets, no game data.
/// </summary>
public class NpcAggressionAndLeashTests
{
    private const uint MapA = 1100;

    // ---- Acquires: the always / by-level / passive decision ---------------------------------------

    [Fact]
    public void Acquires_None_NeverAcquires()
    {
        Assert.False(NpcAggression.Acquires(NpcAggressionMode.None, levelGap: 999, npcLevel: 50, playerLevel: 1));
        Assert.False(NpcAggression.Acquires(NpcAggressionMode.None, levelGap: 999, npcLevel: 50, playerLevel: 50));
    }

    [Fact]
    public void Acquires_Always_AcquiresEveryLevel()
    {
        Assert.True(NpcAggression.Acquires(NpcAggressionMode.Always, levelGap: 0, npcLevel: 50, playerLevel: 1));
        Assert.True(NpcAggression.Acquires(NpcAggressionMode.Always, levelGap: 0, npcLevel: 50, playerLevel: 999));
    }

    [Theory]
    [InlineData(1, true)]    // far below the monster: acquired
    [InlineData(50, true)]   // same level: acquired
    [InlineData(60, true)]   // exactly npcLevel + gap: acquired (<=)
    [InlineData(61, false)]  // one past the gap: ignored
    [InlineData(200, false)] // far above: ignored
    public void Acquires_ByLevel_AcquiresWhilePlayerNotTooFarAbove(int playerLevel, bool expected)
    {
        // Monster level 50, gap 10: it notices a player up to level 60 and stops above that.
        Assert.Equal(expected,
            NpcAggression.Acquires(NpcAggressionMode.ByLevel, levelGap: 10, npcLevel: 50, playerLevel: playerLevel));
    }

    // ---- The custom table file --------------------------------------------------------------------

    [Fact]
    public void Load_ReadsModes_AndDropsNoneRows()
    {
        var path = WriteTable("""
            { "rows": [
              { "asset": 101, "mode": "always" },
              { "asset": 102, "mode": "byLevel", "levelGap": 5 },
              { "asset": 103, "mode": "none" }
            ] }
            """);
        try
        {
            var table = NpcAggression.Load(path);
            Assert.Equal(2, table.Count);   // the none row is not kept

            Assert.True(table.TryGet(101, out var a));
            Assert.Equal(NpcAggressionMode.Always, a.Mode);

            Assert.True(table.TryGet(102, out var b));
            Assert.Equal(NpcAggressionMode.ByLevel, b.Mode);
            Assert.Equal(5, b.LevelGap);

            Assert.False(table.TryGet(103, out _));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("""{ "rows": [ { "asset": 1, "mode": "sometimes" } ] }""")]        // unknown mode
    [InlineData("""{ "rows": [ { "asset": 1, "mode": "byLevel", "levelGap": -1 } ] }""")] // negative gap
    [InlineData("""{ "rows": [ { "asset": 1, "mode": "always" }, { "asset": 1, "mode": "always" } ] }""")] // dup
    public void Load_RejectsMalformedRows(string json)
    {
        var path = WriteTable(json);
        try { Assert.Throws<InvalidDataException>(() => NpcAggression.Load(path)); }
        finally { File.Delete(path); }
    }

    private static string WriteTable(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "npcaggro_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    // ---- LeashReset: clear threat + heal to full --------------------------------------------------

    private static NpcStateManager NewNpcs()
        => new(new GameDataStore(), NullLogger<NpcStateManager>.Instance);

    private static NpcRuntimeState AddMonster(NpcStateManager npcs, int index, int hp)
    {
        npcs.TrackSpawns(MapA, [new SpawnEntry { MapId = MapA, Index = index, AssetIndex = 0, PosX = 500f, PosY = 500f }]);
        var npc = npcs.GetNpc(npcs.AllocateHandle(MapA, index))!;
        npc.Camp = 2;
        npc.MaxHp = hp;
        npc.Hp = hp;
        return npc;
    }

    [Fact]
    public void LeashReset_ClearsThreatAndTargetAndHealsToFull()
    {
        var npcs = NewNpcs();
        var npc = AddMonster(npcs, 1, hp: 1000);
        const uint pc = 0x1234511;   // a PC-tagged attacker handle (low byte 0x11)

        npcs.AddThreat(npc.EntityOid, pc, "Hero", 500);
        npcs.ApplyDamage(npc.EntityOid, 400);   // take it below full
        Assert.Equal(pc, npc.AggroTargetHandle);
        Assert.Equal(600, npc.Hp);

        Assert.True(npcs.LeashReset(npc.EntityOid));
        Assert.Equal(0u, npc.AggroTargetHandle);
        Assert.Equal(1000, npc.Hp);            // healed to full
    }

    [Fact]
    public void LeashReset_NoThreatAndFullHp_IsNoOp()
    {
        var npcs = NewNpcs();
        var npc = AddMonster(npcs, 1, hp: 1000);
        Assert.False(npcs.LeashReset(npc.EntityOid));   // nothing to clear, already full
    }

    [Fact]
    public void LeashReset_UnknownOrDeadHandle_IsFalse()
    {
        var npcs = NewNpcs();
        var npc = AddMonster(npcs, 1, hp: 1000);
        Assert.False(npcs.LeashReset(0xDEAD13));         // unknown handle

        npcs.ApplyDamage(npc.EntityOid, 1000);           // kill it
        Assert.True(npc.IsDead);
        Assert.False(npcs.LeashReset(npc.EntityOid));     // a dead monster does not reset/heal
    }
}
