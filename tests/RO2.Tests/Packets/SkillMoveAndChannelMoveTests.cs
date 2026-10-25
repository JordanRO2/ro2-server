using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The ground-object skill move (1523 ReqSkillMoveNotify, 1525 ReqSkillMoveEnd, 2749 NfySkillMoveEnd) and the
/// move-cancel of an immovable channel (1526 -&gt; 2585). The 1523, 1525 and PC-form 2749 tests pin the wire
/// layouts the handlers read and write. The channel-cancel inputs are representative positions of 1526
/// requests.
/// </summary>
public class SkillMoveAndChannelMoveTests
{
    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    [Fact]
    public void NfySkillMoveEnd2749_PcForm_PutsSkillAndMoveKindWhereTheConsumerReadsThem()
    {
        // Wire layout of 2749 NfySkillMoveEnd: blob+8 caster handle, blob+12 skill, blob+16 move kind,
        // blob+17/21/25 xyz, blob+29 yaw. Body = 01 21 + blob.
        var body = ActorStatePackets.BuildNfySkillMoveEnd(0x07123411u, 630410u, 0x10, 1f, 2f, 3f, 0.5f);
        var expected = Convert.FromHexString(
            "0121" + "2100" + "61e4" + "00000000" + "11341207" + "8a9e0900" + "10"
            + "0000803f" + "00000040" + "00004040" + "0000003f");
        Assert.Equal(expected, body);
    }

    [Fact]
    public void NfySkillMoveEnd2749_RetailOverload_IsThePcFormWithSkill0AndKind0x0E()
    {
        // The NPC form pinned by ActorCrashSweepTests: skill 0, kind 0x0E.
        var expected = Convert.FromHexString(
            "0121210061e40000000013f09d01000000000ea4202444144eaa434d6a3a427e27d3be");
        Assert.Equal(expected, ActorStatePackets.BuildNfySkillMoveEnd(
            0x019DF013u, 0u, ActorStatePackets.RetailNpcMoveKind, F(0x442420A4u), F(0x43AA4E14u), F(0x423A6A4Du), F(0xBED3277Eu)));
        Assert.Equal(expected, ActorStatePackets.BuildNfySkillMoveEnd(
            0x019DF013u, F(0x442420A4u), F(0x43AA4E14u), F(0x423A6A4Du), F(0xBED3277Eu)));
    }

    [Fact]
    public void SkillMoveNotify1523_ParsesTheBuildersLayout()
    {
        // Wire layout of 1523 ReqSkillMoveNotify: +0 target handle, +4 skill, +8 move kind byte,
        // +9/+13/+17 xyz, +21 float 0.0. 25 bytes, NONE family.
        var body = Convert.FromHexString(
            "13f09d01" + "8a9e0900" + "0e" + "0000803f" + "00000040" + "00004040" + "00000000");
        Assert.Equal(SkillMoveNotifyHandler.BodyLength, body.Length);
        Assert.True(SkillMoveNotifyHandler.TryParse(body, out var moved, out var skill, out var kind, out var x, out var y, out var z));
        Assert.Equal(0x019DF013u, moved);
        Assert.Equal(630410u, skill);
        Assert.Equal(0x0E, kind);
        Assert.Equal((1f, 2f, 3f), (x, y, z));
        Assert.False(SkillMoveNotifyHandler.TryParse(body.AsSpan(0, 24), out _, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void SkillMoveTracker_GivesTheMovedActorToTheEndOnce()
    {
        var caster = new PlayerState { EntityId = 0x00100111u };
        SkillMoveTracker.Record(caster, 630410, new SkillMoveTracker.SkillMove(0x00205D13u, 0x0E, 1f, 2f, 3f));
        SkillMoveTracker.Record(caster, 630410, new SkillMoveTracker.SkillMove(0x00205D13u, 0x0E, 4f, 5f, 6f)); // a later step replaces it

        Assert.False(SkillMoveTracker.TryTake(caster, 630405, out _)); // another skill
        Assert.True(SkillMoveTracker.TryTake(caster, 630410, out var move));
        Assert.Equal(new SkillMoveTracker.SkillMove(0x00205D13u, 0x0E, 4f, 5f, 6f), move);
        Assert.False(SkillMoveTracker.TryTake(caster, 630410, out _)); // taken by the 1525
        Assert.False(SkillMoveTracker.TryTake(new PlayerState(), 630410, out _));
    }

    [Fact]
    public void ApplyToMovedActor_MovesTheCasterOnlyWhenItIsTheNamedActor()
    {
        var npcs = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance);
        var guard = new MovementGuard(new SystemClock());
        var caster = new PlayerState { EntityId = 0x00100111u, X = 10f, Y = 20f, Z = 30f };
        UseShippedReachTable();

        Assert.False(SkillMoveNotifyHandler.ApplyToMovedActor(caster, npcs, 630401, 9, 0x00100211u, 1f, 2f, 3f, NullLogger.Instance, "move", guard));
        Assert.Equal((10f, 20f, 30f), (caster.X, caster.Y, caster.Z)); // another PC: not taken

        // The self-move needs an accepted cast of this skill on the caster's map; kind 9 stores the cast target, so
        // that cast must have targeted the caster itself. (10,20) -> (1,2) is ~20 units, inside ReachFor(6304) = 39.
        SkillMoveTracker.RecordCast(caster, caster.MapId, 630401, caster.EntityId);
        Assert.True(SkillMoveNotifyHandler.ApplyToMovedActor(caster, npcs, 630401, 9, caster.EntityId, 1f, 2f, 3f, NullLogger.Instance, "move", guard));
        Assert.Equal((1f, 2f, 3f), (caster.X, caster.Y, caster.Z)); // the caster: a server-granted skill move, taken
        SkillMoveTracker.ReachTableOverride = null;
    }

    /// <summary>The reach gate reads Skill_Table_Base through a cwd-relative path a test host cannot see; hand it the
    /// shipped file for this thread (SkillMoveTracker.ReachTableOverride).</summary>
    private static void UseShippedReachTable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "Skill_Table_Base.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        SkillMoveTracker.ReachTableOverride = SkillMoveTracker.LoadReach(Path.Combine(dir!.FullName, "data", "Skill_Table_Base.json"));
    }

    [Fact]
    public void NpcMoveRefusal_TakesOnlyTheLatestCastTargetOfTheSameMap()
    {
        UseShippedReachTable();
        var spawn = new SpawnEntry { MapId = 1300, Index = 383, PosX = 447.5f, PosY = 321.1f, PosZ = 12.4f, AssetIndex = 28580000 };
        var npcs = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance);
        npcs.TrackSpawns(spawn.MapId, [spawn]);
        var handle = npcs.AllocateHandle(spawn.MapId, spawn.Index);
        var npc = npcs.GetNpc(handle)!;
        npc.Camp = 2;

        var elsewhere = new PlayerState { EntityId = 0x00100111u, MapId = 1100 };
        SkillMoveTracker.RecordCast(elsewhere, 1100, 630401, handle);
        Assert.False(SkillMoveNotifyHandler.ApplyToMovedActor(elsewhere, npcs, 630401, 9, handle, 1f, 2f, 3f, NullLogger.Instance, "move", new MovementGuard(new SystemClock())));
        Assert.Equal((447.5f, 321.1f), (npc.PosX, npc.PosY)); // another map: not taken

        var caster = new PlayerState { EntityId = 0x00100112u, MapId = 1300, X = 440f, Y = 320f };
        Assert.Equal("not the target of the caster's latest accepted cast of this skill",
            SkillMoveNotifyHandler.NpcMoveRefusal(caster, npcs, 630401, 9, handle, 445f, 321f, out _));
        SkillMoveTracker.RecordCast(caster, 1300, 630401, handle);
        Assert.Equal("move kind 3 never stores a cast target",
            SkillMoveNotifyHandler.NpcMoveRefusal(caster, npcs, 630401, 3, handle, 445f, 321f, out _));
        SkillMoveTracker.RecordCast(caster, 1300, 630402, handle);
        Assert.Null(SkillMoveNotifyHandler.NpcMoveRefusal(caster, npcs, 630401, 9, handle, 445f, 321f, out _)); // a cast of ANOTHER skill does not evict it
        SkillMoveTracker.RecordCast(caster, 1300, 630401, 0x00205D99u);
        Assert.NotNull(SkillMoveNotifyHandler.NpcMoveRefusal(caster, npcs, 630401, 9, handle, 445f, 321f, out _)); // a newer cast of the SAME skill replaced it
        Assert.Equal((447.5f, 321.1f), (npc.PosX, npc.PosY));
        SkillMoveTracker.ReachTableOverride = null;
    }

    [Fact]
    public void LoadReach_IsSkillRangePlusInfluenceBound()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "Skill_Table_Base.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var reach = SkillMoveTracker.LoadReach(Path.Combine(dir!.FullName, "data", "Skill_Table_Base.json"));
        Assert.Equal(35f, reach[6304]); // Skill_Range 25 + Influence_Bound_Length 10
    }

    [Theory]
    // A 1526 walk during a 6304 channel, answered by its own 2585: the avatar moved.
    [InlineData(668.52734375f, 893.0862426757812f, 668.52734375f, 893.0862426757812f, 670.1732788085938f, 895.5944213867188f, true)]
    // Destination == current point == the last point: a turn on the spot, which must not cancel.
    [InlineData(138.97747802734375f, 353.8855895996094f, 138.97747802734375f, 353.8855895996094f, 138.97747802734375f, 353.8855895996094f, false)]
    // Arrived (destination == current) but somewhere else than the last point held: the PC did move.
    [InlineData(170.0f, 323.28570556640625f, 176.30885314941406f, 323.28570556640625f, 176.30885314941406f, 323.28570556640625f, true)]
    // Below the 0.1 arrival tolerance.
    [InlineData(100f, 100f, 100.05f, 100f, 100.05f, 100.05f, false)]
    public void DisplacesChannel_IsAMoveOfTheAvatar(float lastX, float lastY, float curX, float curY, float destX, float destY, bool moved)
    {
        Assert.Equal(moved, ViewGroupMoveHandler.DisplacesChannel(lastX, lastY, curX, curY, destX, destY));
    }

    [Fact]
    public void ImmovableGroups_HoldBothChannelledGroups()
    {
        var set = SkillChannelingPackets.LoadImmovableSkillGroups(Path.Combine(RequireDataDirectory(), "Skill_Table_Base.json"));
        Assert.NotNull(set);
        Assert.Contains(6304u, set);   // 630401..630410
        Assert.Contains(73721u, set);  // 7372101
    }

    [Fact]
    public void ImmovableGroups_MissingTable_IsNull()
    {
        Assert.Null(SkillChannelingPackets.LoadImmovableSkillGroups(Path.Combine(Path.GetTempPath(), "no-such-dir", "Skill_Table_Base.json")));
    }

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Skill_Table_Base.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "no ancestor of " + AppContext.BaseDirectory + " holds data/Skill_Table_Base.json");
    }
}
