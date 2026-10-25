using System.Buffers.Binary;
using System.Text.Json;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for 1698 multi-talk objectives, 2487 OnUpdatePCSaveData, 2581 NfyKharaFailed, 2724 NfyGuildSkillLearn and
/// 2742 AnsReqErrorFiledPvP, checked against the shipped data/*.json. Expected frames are retail frames from the
/// reference frames.
/// </summary>
public class UnitA6RetailFramesTests
{
    
    [Fact]
    public void MultiTalkQuest_UpdatesEveryMeetingAndClosesAtTheLastRow()
    {
        
        
        var rows = QuestRows(112210011);
        var player = new PlayerState();
        player.ActiveQuests.Add(112210011);
        QuestTalkProgress.Reset(player, 112210011);

        Assert.False(QuestTalkProgress.Meet(player, 112210011, rows, 32650000)!.Value.AllMet);
        Assert.False(QuestTalkProgress.Meet(player, 112210011, rows, 32650000)!.Value.AllMet);
        Assert.False(QuestTalkProgress.Meet(player, 112210011, rows, 32650000)!.Value.AllMet);
        Assert.False(QuestTalkProgress.Meet(player, 112210011, rows, 32680000)!.Value.AllMet);
        Assert.False(QuestTalkProgress.Meet(player, 112210011, rows, 32770000)!.Value.AllMet);
        Assert.True(QuestTalkProgress.Meet(player, 112210011, rows, 32710000)!.Value.AllMet);
        
        Assert.Null(QuestTalkProgress.Meet(player, 112210011, rows, 32380000));

        Assert.Equal("01131300bbe3000000005b30b006011033f2010100", Hex(QuestNotifyPackets.BuildQuestUpdateNotify2432(112210011, 32650000)));
        Assert.Equal("01131300bbe3000000005b30b0060140a8f2010100", Hex(QuestNotifyPackets.BuildQuestUpdateNotify2432(112210011, 32680000)));
        Assert.Equal("01131300bbe3000000005b30b00601d007f4010100", Hex(QuestNotifyPackets.BuildQuestUpdateNotify2432(112210011, 32770000)));
        Assert.Equal("01131300bbe3000000005b30b00601701df3010100", Hex(QuestNotifyPackets.BuildQuestUpdateNotify2432(112210011, 32710000)));
        Assert.Equal("01101000bce3000000005b30b00600000000", Hex(QuestProgressPackets.BuildQuestComplete2427(112210011)));
    }

    [Fact]
    public void MultiTalkQuest_ResetAndPurgeForgetTheMetRows()
    {
        var rows = QuestRows(112210011);
        var player = new PlayerState();
        player.ActiveQuests.Add(112210011);
        foreach (var npc in new uint[] { 32680000, 32770000, 32650000 })
            QuestTalkProgress.Meet(player, 112210011, rows, npc);

        QuestTalkProgress.Reset(player, 112210011);   
        Assert.False(QuestTalkProgress.Meet(player, 112210011, rows, 32710000)!.Value.AllMet);

        foreach (var npc in new uint[] { 32680000, 32770000, 32650000 })
            QuestTalkProgress.Meet(player, 112210011, rows, npc);
        player.ActiveQuests.Remove(112210011);        
        QuestTalkProgress.Purge(player);
        player.ActiveQuests.Add(112210011);
        Assert.False(QuestTalkProgress.Meet(player, 112210011, rows, 32710000)!.Value.AllMet);
    }

    [Fact]
    public void SingleOrClientReportedTalkRows_AreNotTracked()
    {
        var player = new PlayerState();
        
        Assert.Null(QuestTalkProgress.Meet(player, 111150017, QuestRows(111150017), 33130000));
        
        Assert.Null(QuestTalkProgress.Meet(player, 914000120, QuestRows(914000120), 99800013));
        Assert.Empty(QuestTalkProgress.TrackedRows(QuestRows(914000240)));
        Assert.Equal(4, QuestTalkProgress.TrackedRows(QuestRows(112210007)).Length);
    }

    
    [Theory]
    
    
    [InlineData("02d500d50095e31127560dde859ba7b3b6e00d1127560d4a006100790061004e006f0076006100000065000000000000000000000000000000000000000000000001010f0cff00070000000301ff00033200000000000000000c0000f2380000890d0000e3743844da4a3944545b5d424c0400009a015500000000e52d0400000000004914000000013c0000000232000000000000000021001a00000000d50000009600000000000000000000000000ffffffffffffffff00000000ffffffffffffffff000000000f02ddf4640000000000000000000000")]
    [InlineData("02d500d50095e31184230e34209ba7b3b6e00d1184230e4b00610079006100740065004e006f007600610000000000000000000000000000000000000000000000010108010000050000000606ff00043200000000000000000d000091cf00003328000013153444f85d3a4461675d424c0400000d000100000000027f231e02000000497800000001cd0000000332000000000000000025000000000000d70000007701000000000000000000000000ffffffffffffffff650000007e12545400000000ca000000280425b20b00000000006414b2030000")]
    [InlineData("02d500d50095e31175460ca2c4a3a7b3b6e00d1175460c6c004f004d004500470041006c000000000000000000000000000000000000000000000000000000000001010803ff00050000000211ff00043200000000000000000600006e8b00000c210000851153440f404444d8625d424c04000067006c0000000073800300000000004950000000015901000003320000000000000000210000000000008e00000000000000000000005e0100000000ffffffffffffffff650000000e04915500000000cd0000002304614c0e0000000000641423030000")]
    public void PcSaveData2487_ReproducesRetailFrames(string retailHex)
    {
        var frame = Convert.FromHexString(retailHex);
        var handle = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(7));
        var head = frame.AsSpan(11, PcSaveDataPackets.CharacterHeadLength).ToArray();
        Assert.Equal(handle, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8)));   
        Assert.Equal(retailHex, Hex(PcSaveDataPackets.BuildOnUpdatePCSaveData2487(handle, head)));
    }

    [Fact]
    public void PcSaveData2487_HeadCarriesTheLiveState()
    {
        var ch = new Character { Id = 7, Name = "JayaNova", Level = 1, Gold = 5 };
        var player = new PlayerState
        {
            CharacterId = 7, CharacterUid = 1000000000003638750, EntityId = 0x0D562711,
            Level = 50, Exp = 1234, Hp = 14578, Mp = 3465, Gold = 273893,
            MapId = 1100, X = 737.8264f, Y = 741.16956f, Z = 55.33919f,
        };
        var head = PcSaveDataPackets.BuildLiveHead205(player, ch, talentPointsRemaining: 26);

        Assert.Equal(205, head.Length);
        Assert.Equal(1000000000003638750ul, BinaryPrimitives.ReadUInt64LittleEndian(head));
        Assert.Equal(0x0D562711u, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8)));
        Assert.Equal("JayaNova", System.Text.Encoding.Unicode.GetString(head, 12, 42).TrimEnd('\0'));
        Assert.Equal(50, head[69]);
        
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(70)));
        Assert.Equal(14578u, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(81)));
        Assert.Equal(3465u, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(85)));
        Assert.Equal(1100u, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(101)));
        Assert.Equal(273893ul, BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(112)));
        Assert.Equal(26, head[142]);

        var body = PcSaveDataPackets.BuildOnUpdatePCSaveData2487(player.EntityId, head);
        Assert.Equal("02d500d50095e3", Hex(body)[..14]);
        Assert.Equal(216, body.Length);
    }

    
    [Fact]
    public void KharaFailed2581_ReproducesRetailFrames()
    {
        
        Assert.Equal("7d010000d254", Hex(KharaFailPackets.BuildNfyKharaFailed2581(381)));
        Assert.Equal("80010000d254", Hex(KharaFailPackets.BuildNfyKharaFailed2581(384)));
        Assert.Equal("89010000d254", Hex(KharaFailPackets.BuildNfyKharaFailed2581(393)));
    }

    [Fact]
    public void KharaFailed_OnlyTheFailType49KharasFail_InAscendingOrder()
    {
        var catalog = new KharaCatalog(Load<KharaInfo>("Khara_Info.json"));
        Assert.Equal(new uint[] { 381, 384, 393 },
            KharaFailPackets.FailingKharas(new uint[] { 393, 284, 381, 500, 384, 589 }, catalog));
        Assert.Empty(KharaFailPackets.FailingKharas(new uint[] { 284, 286, 288 }, catalog));
    }

    
    [Fact]
    public void GuildSkillLearn2724_ReproducesRetailFrame()
    {
        
        
        var table = GuildSkillLearnPackets.Index(Load<GuildSkillLevel>("Guild_Skill_Level.json"));
        foreach (var request in new[]
        {
            new uint[] { 120401, 120402, 120403, 120404, 120405, 120501, 120601 },
            new uint[] { 120501, 120601, 120401, 120402, 120403, 120404, 120405 },
        })
        {
            var learned = GuildSkillLearnPackets.PlanLearn(request, Array.Empty<uint>(), 20, table);
            Assert.NotNull(learned);
            var ids = GuildSkillLearnPackets.TopIdPerLine(learned);
            Assert.Equal(new uint[] { 120501, 120601, 120405 }, ids);
            var points = GuildSkillLearnPackets.UnspentPoints(20, learned, table);
            Assert.Equal(12, points);
            Assert.Equal("0f0000000c0300b5d6010019d7010055d60100", Hex(GuildSkillLearnPackets.BuildNfyGuildSkillLearn2724(points, ids)));
        }
    }

    [Theory]
    
    [InlineData(23, 1, new uint[] { 120105, 120405, 120505, 120605, 130201, 200105, 210107, 210207, 210307, 210407, 210503, 220105, 220205, 220304, 220409, 220510, 220610, 220710 })]
    [InlineData(20, 0, new uint[] { 120105, 120405, 120504, 120605, 200105, 210107, 210207, 210307, 210407, 210503, 220105, 220205, 220305, 220410, 220510, 220610, 220710 })]
    [InlineData(50, 28, new uint[] { 120105, 120405, 120505, 120605, 130201, 200105, 210107, 210207, 210307, 210407, 210503, 220105, 220205, 220305, 220410, 220510, 220610, 220710 })]
    [InlineData(20, 12, new uint[] { 120405, 120501, 120601 })]
    public void UnspentPoints_MatchesRetail2559(int guildLevel, int points, uint[] learned)
    {
        var table = GuildSkillLearnPackets.Index(Load<GuildSkillLevel>("Guild_Skill_Level.json"));
        Assert.Equal(points, GuildSkillLearnPackets.UnspentPoints((byte)guildLevel, learned, table));
    }

    [Fact]
    public void GuildSkillLearn_RefusesAWholeRequestThatCannotBeLearned()
    {
        var table = GuildSkillLearnPackets.Index(Load<GuildSkillLevel>("Guild_Skill_Level.json"));
        
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 120501, 120601, 120401 }, Array.Empty<uint>(), 19, table));
        
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 120502 }, Array.Empty<uint>(), 20, table));
        
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 120501 }, new uint[] { 120501 }, 20, table));
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 120501, 120501 }, Array.Empty<uint>(), 20, table));
        
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 220101 }, Array.Empty<uint>(), 20, table));
        
        Assert.Null(GuildSkillLearnPackets.PlanLearn(new uint[] { 120101, 120102, 120103, 120104, 120105 }, Array.Empty<uint>(), 5, table));
        Assert.NotNull(GuildSkillLearnPackets.PlanLearn(new uint[] { 120101, 120102, 120103, 120104 }, Array.Empty<uint>(), 5, table));
        
        Assert.Equal(new uint[] { 120503 },
            GuildSkillLearnPackets.PlanLearn(new uint[] { 120503 }, new uint[] { 120501, 120502 }, 20, table));
    }

    [Fact]
    public void GuildSkillLearn1512_ParsesTheClientShape()
    {
        
        var body = Convert.FromHexString("0a0000000200b5d6010019d70100");
        Assert.True(GuildSkillLearnPackets.TryParseRequest(body, out var ids));
        Assert.Equal(new uint[] { 120501, 120601 }, ids);
        Assert.False(GuildSkillLearnPackets.TryParseRequest(Convert.FromHexString("0b0000000200b5d6010019d70100"), out _));
        Assert.False(GuildSkillLearnPackets.TryParseRequest(Convert.FromHexString("0a0000000200b5d60100"), out _));
    }

    
    [Fact]
    public void FieldPvPReject2742_ReproducesRetailFrames()
    {
        
        Assert.Equal("010a0a005de411ad2b070c00", Hex(FieldPvPRejectPackets.BuildReject2742(0x072BAD11, FieldPvPRejectPackets.TargetInBattle)));
        Assert.Equal("010a0a005de4119b1a080d00", Hex(FieldPvPRejectPackets.BuildReject2742(0x081A9B11, FieldPvPRejectPackets.TargetOnBattleCue)));
    }

    [Fact]
    public void FieldPvPRefusal_CodeFollowsTheTargetsDuel()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        PlayerState Pc(uint session) => new() { SessionId = session, EntityId = PlayerStateManager.BuildActorHandle(session), MapId = 1100 };
        var a = Pc(0x7A6001); var b = Pc(0x7A6002); var c = Pc(0x7A6003); var d = Pc(0x7A6004);
        foreach (var p in new[] { a, b, c, d }) players.AddPlayer(p.SessionId, p);

        Assert.Null(FieldPvPHandler.RefusalCode(a, b, players));
        var duel = FieldPvPService.Open(b, c, players);
        Assert.NotNull(duel);
        Assert.Equal(FieldPvPRejectPackets.TargetOnBattleCue, FieldPvPHandler.RefusalCode(a, b, players));
        Assert.True(FieldPvPService.TryAccept(duel));
        Assert.Equal(FieldPvPRejectPackets.TargetInBattle, FieldPvPHandler.RefusalCode(a, c, players));
        
        Assert.Null(FieldPvPHandler.RefusalCode(b, a, players));
        d.IsDead = true;
        Assert.Equal(FieldPvPRejectPackets.TargetDead, FieldPvPHandler.RefusalCode(a, d, players));
        Assert.Equal(FieldPvPRejectPackets.ChallengerDead, FieldPvPHandler.RefusalCode(d, a, players));
        FieldPvPService.TryClose(duel.Id, out _);
    }

    
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private sealed record QuestRow(uint Quest_ID, byte Complete_Type, uint Complete_Type_Value1, uint Complete_Type_Value2, uint Complete_Type_Value3);

    private static readonly Lazy<List<QuestRow>> QuestInfo = new(() => Load<QuestRow>("Quest_Info.json"));

 /// <summary>A quest's Quest_Info rows in file order, as GameDataStore.QuestRows groups them.</summary>
    private static QuestData[] QuestRows(uint questId) => QuestInfo.Value
        .Where(r => r.Quest_ID == questId)
        .Select(r => new QuestData
        {
            Quest_ID = r.Quest_ID, Complete_Type = r.Complete_Type, Complete_Type_Value1 = r.Complete_Type_Value1,
            Complete_Type_Value2 = r.Complete_Type_Value2, Complete_Type_Value3 = r.Complete_Type_Value3,
        })
        .ToArray();

    
    
    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static List<T> Load<T>(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", file)))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + file);
        return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(Path.Combine(dir.FullName, "data", file)),
            DataJsonOptions)!;
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
