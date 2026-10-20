using RO2.Domain.Entities;
using System.Text.Json;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Verifies server-driven Khara progress and completion (<see cref="KharaEngine"/>) against the shipped data/Khara_Info.json.
/// The expected frame sequences in each test are reference payloads.
/// </summary>
public class KharaCompletionTests
{
    private static readonly Lazy<KharaCatalog> Catalog = new(() => new KharaCatalog(LoadRows()));

    
    [Fact]
    public void Builders_ReproduceRetailFrames()
    {
        
        Assert.Equal("f00100000000", Hex(KharaService.BuildAnsKharaAccept2574(496, 0)));
        
        Assert.Equal("f0010000", Hex(KharaService.BuildNfyKharaComplete2575(496)));
        Assert.Equal("0a00000000000000", Hex(KharaService.BuildNfyKharaReward2576(10)));
        
        Assert.Equal("5701000000805a13030400", Hex(KharaService.BuildNfyKharaInComplete2577(343, 0, 51600000, 4)));
        
        Assert.Equal("8d01000000b731b7001600", Hex(KharaService.BuildNfyKharaInComplete2577(397, 0, 12005815, 22)));
        
        Assert.Equal("22010000", Hex(KharaService.BuildPointChange2502(290)));
    }

    
    [Fact]
    public void Accept_HeldItemKhara_SendsProgressThenSpendThenCompletion()
    {
        
        
        var state = new KharaStateDocument { Id = 1, Points = 320 };
        var engine = Engine(state, held: new() { [22310851] = 1 });

        Assert.True(engine.Accept(589));

        Assert.Equal(new[]
        {
            "2577:4d02000000c36f54010100",
            "2502:22010000",
            "2575:4d020000",
            "2576:0000000000000000",
        }, Frames(engine));
        Assert.Equal(290u, engine.State.Points);
        Assert.Contains(589u, engine.State.Completed);
        Assert.Null(engine.State.FindActive(589));
    }

    [Fact]
    public void Accept_LastKharaOfABoard_CompletesTheBoardToo_AndTheBalanceStopsAtZero()
    {
        
        
        
        var state = new KharaStateDocument
        {
            Id = 1,
            Points = 5,
            Completed = new List<uint> { 371, 372, 373, 374 },
            Active = new List<KharaActiveDocument> { Active(386, 0, 0, 0, 0, 0) },
        };
        var engine = Engine(state, held: new() { [22310636] = 1, [22310637] = 1, [22310638] = 1, [22310664] = 1 });

        Assert.True(engine.Accept(375));

        Assert.Equal(new[]
        {
            "2577:7701000000ec6e54010100",
            "2577:7701000001ed6e54010100",
            "2577:7701000002ee6e54010100",
            "2577:7701000003086f54010100",
            "2502:00000000",
            "2575:77010000",
            "2576:0000000000000000",
            "2575:82010000",
            "2576:0000000000000000",
        }, Frames(engine));
        Assert.Equal(0u, engine.State.Points);
        Assert.Empty(engine.State.Active);
        Assert.Equal(new uint[] { 371, 372, 373, 374, 375, 386 }, engine.State.Completed);
    }

    [Fact]
    public void Accept_QuestKharaWhoseQuestIsDone_CompletesInTheAcceptBurst()
    {
        
        
        var state = new KharaStateDocument { Id = 1, Points = 75 };
        var engine = Engine(state, quests: new() { 212013690 });

        Assert.True(engine.Accept(394));

        Assert.Equal(new[] { "2502:41000000", "2575:8a010000", "2576:1e00000000000000" }, Frames(engine));
        Assert.Equal(95u, engine.State.Points);
    }

    [Fact]
    public void Accept_NothingHeldYet_OnlySpendsThePoints()
    {
        
        
        var state = new KharaStateDocument { Id = 1, Points = 95 };
        var engine = Engine(state);

        Assert.True(engine.Accept(372));

        Assert.Equal(new[] { "2502:41000000" }, Frames(engine));
        var entry = engine.State.FindActive(372);
        Assert.NotNull(entry);
        Assert.Equal(KharaService.StateInProgress, entry!.State);
        Assert.Equal(new uint[] { 0, 0, 0 }, entry.Counts);
    }

    [Fact]
    public void Accept_BoardKhara_SendsNothingAfterThe2574()
    {
        
        
        
        var engine = Engine(new KharaStateDocument { Id = 1 });

        Assert.True(engine.Accept(284));

        Assert.Empty(engine.Frames);
        Assert.Equal(9, engine.State.FindActive(284)!.Counts.Count);
    }

    [Fact]
    public void Accept_AlreadyAcceptedCompletedOrUnknown_ChangesNothing()
    {
        var state = new KharaStateDocument
        {
            Id = 1,
            Points = 100,
            Completed = new List<uint> { 589 },
            Active = new List<KharaActiveDocument> { Active(372, 0, 0, 0) },
        };
        var engine = Engine(state, held: new() { [22310851] = 1 });

        Assert.False(engine.Accept(589));    
        Assert.False(engine.Accept(372));    
        Assert.False(engine.Accept(65000));  
        Assert.Empty(engine.Frames);
        Assert.False(engine.Changed);
        Assert.Equal(100u, engine.State.Points);
    }

    
    [Fact]
    public void QuestCompleted_AutoAcceptsAndCompletesAOneRowKhara()
    {
        
        
        var engine = Engine(new KharaStateDocument { Id = 1 }, quests: new() { 911000270 }, level: 1);

        engine.OnQuestCompleted(911000270);

        Assert.Equal(new[] { "2574:f00100000000", "2575:f0010000", "2576:0a00000000000000" }, Frames(engine));
        Assert.Equal(10u, engine.State.Points);
        Assert.Equal(new uint[] { 496 }, engine.State.Completed);
    }

    [Fact]
    public void QuestCompleted_AutoAcceptsAMultiRowKhara_WithoutCompletingIt()
    {
        
        
        var engine = Engine(new KharaStateDocument { Id = 1 }, quests: new() { 111150004 }, level: 50);

        engine.OnQuestCompleted(111150004);

        Assert.Equal(new[] { "2574:300200000000" }, Frames(engine));
        Assert.Equal(7, engine.State.FindActive(560)!.Counts.Count);
    }

    [Fact]
    public void QuestCompleted_NoAutoAcceptBelowTheLevelWindow_OrForAManualKhara()
    {
        
        var low = Engine(new KharaStateDocument { Id = 1 }, quests: new() { 111150004 }, level: 49);
        low.OnQuestCompleted(111150004);
        Assert.Empty(low.Frames);

        
        var manual = Engine(new KharaStateDocument { Id = 1 }, quests: new() { 212013690 }, level: 60);
        manual.OnQuestCompleted(212013690);
        Assert.Empty(manual.Frames);
        Assert.False(manual.Changed);
    }

    
    [Fact]
    public void Kill_StepsTheMatchingRowByOne_AndStopsAtTheTarget()
    {
        
        
        var state = new KharaStateDocument { Id = 1, Active = new List<KharaActiveDocument> { Active(343, 3, 0, 0, 0) } };
        var engine = Engine(state);

        engine.OnNpcKilled(51600000);
        engine.OnNpcKilled(51600000);   
        engine.OnNpcKilled(49999999);   

        Assert.Equal(new[] { "2577:5701000000805a13030400" }, Frames(engine));
        Assert.Equal(new uint[] { 4, 0, 0, 0 }, engine.State.FindActive(343)!.Counts);
    }

    [Fact]
    public void Kill_ThatMeetsTheLastRow_Completes()
    {
        
        var state = new KharaStateDocument { Id = 1, Active = new List<KharaActiveDocument> { Active(580, 3, 3, 3, 2) } };
        var engine = Engine(state);

        engine.OnNpcKilled(50003766);

        Assert.Equal(new[] { "2577:440200000336fffa020300", "2575:44020000", "2576:0000000000000000" }, Frames(engine));
        Assert.Contains(580u, engine.State.Completed);
    }

    
    [Fact]
    public void HeldCount_FollowsTheBag_UpAndDown()
    {
        
        
        
        var held = new Dictionary<uint, int> { [12005815] = 22 };
        var state = new KharaStateDocument { Id = 1, Active = new List<KharaActiveDocument> { Active(397, 20) } };
        var engine = Engine(state, held: held);

        engine.OnHeldCountChanged(12005815);
        held[12005815] = 0;
        engine.OnHeldCountChanged(12005815);
        engine.OnHeldCountChanged(12005815);   

        Assert.Equal(new[] { "2577:8d01000000b731b7001600", "2577:8d01000000b731b7000000" }, Frames(engine));
        Assert.NotNull(engine.State.FindActive(397));
    }

    [Fact]
    public void HeldCount_ItemsThatLeftUnreported_DoNotCompleteTheKhara()
    {
        
        
        
        
        
        var held = new Dictionary<uint, int>();
        var state = new KharaStateDocument { Id = 1, Active = new List<KharaActiveDocument> { Active(325) } };
        var engine = Engine(state, held: held);

        held[22281303] = 20;
        engine.OnHeldCountChanged(22281303);
        held[22281303] = 0;          
        held[22281302] = 20;
        engine.OnHeldCountChanged(22281302);

        Assert.Equal(new[]
        {
            "2577:450100000157fc53011400",   
            "2577:450100000056fc53011400",   
            "2577:450100000157fc53010000",   
        }, Frames(engine));
        Assert.Equal(new uint[] { 20, 0 }, engine.State.FindActive(325)!.Counts);
        Assert.Empty(engine.State.Completed);
        Assert.Equal(0u, engine.State.Points);
    }

    [Fact]
    public void Progress_WithoutCompletion_IsAStateChange()
    {
        
        
        var kill = Engine(new KharaStateDocument { Id = 1, Active = new List<KharaActiveDocument> { Active(343, 3, 0, 0, 0) } });
        kill.OnNpcKilled(51600000);
        Assert.True(kill.Changed);
        Assert.Equal(4u, kill.State.FindActive(343)!.Counts[0]);

        var loot = Engine(new KharaStateDocument { Id = 1, Active = new List<KharaActiveDocument> { Active(397, 20) } },
            held: new() { [12005815] = 22 });
        loot.OnHeldCountChanged(12005815);
        Assert.True(loot.Changed);

        var none = Engine(new KharaStateDocument { Id = 1, Active = new List<KharaActiveDocument> { Active(397, 22) } },
            held: new() { [12005815] = 22 });
        none.OnHeldCountChanged(12005815);   
        Assert.False(none.Changed);
        Assert.Empty(none.Frames);
    }

    [Fact]
    public void TryComplete_OnlyWhenEveryConditionHolds()
    {
        var state = new KharaStateDocument { Id = 1, Active = new List<KharaActiveDocument> { Active(343, 4, 4, 4, 3) } };
        var engine = Engine(state);
        Assert.False(engine.TryComplete(343));
        Assert.False(engine.TryComplete(580));   
        Assert.Empty(engine.Frames);

        engine.State.FindActive(343)!.Counts[3] = 4;
        Assert.True(engine.TryComplete(343));
        Assert.Equal(new[] { "2575:57010000", "2576:0000000000000000" }, Frames(engine));
    }

    
    [Fact]
    public void LoginVectors_ReproduceARetail2310()
    {
        
        
        
        const string retail =
            "0a00b4000000020000000000000003000000000001000000000000001c010000020000000000000009000000000000000000000000000000000000000000000000000000000000000000000000001e0100000200000000000000090000000000000000000000000000000000000000000000000000000000000000000000000020010000020000000000000012000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002101000002000000000000000900000000000000000000000000000000000000000000000000000000000000000000000000220100000200000000000000090000000000000000000000000000000000000000000000000000000000000000000000000023010000020000000000000009000000000000000000000000000000000000000000000000000000000000000000000000002401000002000000000000000900000000000000000000000000000000000000000000000000000000000000000000000000f301000002000000000000000900000000000000000000000000000000000000000000000000000000000000000000000000f4010000020000000000000009000000000000000000000000000000000000000000000000000000000000000000000000000100f0010000";
        var state = new KharaStateDocument
        {
            Id = 1,
            Points = 10,
            Active = new List<KharaActiveDocument>
            {
                Active(180, 0, 1, 0),
                Active(284), Active(286), Active(288), Active(289), Active(290),
                Active(291), Active(292), Active(499), Active(500),
            },
            Completed = new List<uint> { 496 },
        };

        using var w = new PacketWriter();
        KharaService.WriteLoginVectors(w, state, Catalog.Value);

        Assert.Equal(retail, Hex(w.ToArray()));
    }

    [Fact]
    public void LoginVectors_WithoutState_AreTwoEmptyVectors()
    {
        
        
        using var w = new PacketWriter();
        KharaService.WriteLoginVectors(w, (KharaStateDocument?)null, Catalog.Value);
        Assert.Equal("00000000", Hex(w.ToArray()));
    }

    [Fact]
    public void ConditionRows_MatchTheRetailProgressVectorLength()
    {
        
        
        Assert.Equal(18, Catalog.Value.Get(288)!.Conditions.Count);
        Assert.Equal(9, Catalog.Value.Get(423)!.Conditions.Count);
        Assert.Equal(3, Catalog.Value.Get(180)!.Conditions.Count);
        Assert.Single(Catalog.Value.Get(44)!.Conditions);
    }

    
    private static KharaEngine Engine(KharaStateDocument state, Dictionary<uint, int>? held = null,
        HashSet<uint>? quests = null, byte level = 50)
    {
        held ??= new Dictionary<uint, int>();
        quests ??= new HashSet<uint>();
        return new KharaEngine(Catalog.Value, state, new KharaFacts
        {
            Level = level,
            HeldCount = id => held.GetValueOrDefault(id),
            IsQuestCompleted = quests.Contains,
        });
    }

    private static KharaActiveDocument Active(uint kharaId, params uint[] counts)
    {
        var rows = Catalog.Value.Get(kharaId)!.Conditions.Count;
        var list = new List<uint>(counts);
        while (list.Count < rows) list.Add(0);
        return new KharaActiveDocument { KharaId = kharaId, State = KharaService.StateInProgress, Counts = list };
    }

    private static string[] Frames(KharaEngine engine) =>
        engine.Frames.Select(f => $"{f.Opcode}:{Hex(f.Body)}").ToArray();

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    
    
    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static List<KharaInfo> LoadRows()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Khara_Info.json")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Khara_Info.json");
        var json = File.ReadAllText(Path.Combine(dir.FullName, "data", "Khara_Info.json"));
        return JsonSerializer.Deserialize<List<KharaInfo>>(json,
            DataJsonOptions)!;
    }
}
