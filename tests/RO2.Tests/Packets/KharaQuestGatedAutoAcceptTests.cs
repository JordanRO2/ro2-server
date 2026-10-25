using RO2.Domain.Entities;
using System.Text.Json;
using RO2.GameData.Tables;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the quest-gated Complete_Type 2 auto Khara (main row Condition_Type 8, <see cref="KharaDefinition.GateQuest"/>)
/// against the shipped data/Khara_Info.json.
/// </summary>
/// <remarks>
/// A gated Khara is accepted when its quest completes (2429), with a bare 2574 and no 2577, and not by a kill before that.
/// Its first later kill sends a 2577 with count 1. An ungated Khara is accepted by its first kill: a 2574, then a 2577 with count 1.
/// The frames are built by the verified builders in KharaCompletionTests.
/// </remarks>
public class KharaQuestGatedAutoAcceptTests
{
    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static readonly Lazy<KharaCatalog> Catalog = new(() => new KharaCatalog(LoadRows()));

    
    
    private const uint GatedKhara = 44, GateQuest = 112013020, GatedNpc = 42140000;

    [Fact]
    public void TheCatalog_IndexesTheNinetySixGatedKharas_ByTheirQuest()
    {
        var gated = LoadRows().Where(r => r.Condition_Type == KharaService.ConditionQuestDone).Select(r => (uint)r.Quest_ID)
            .Distinct().ToList();
        Assert.Equal(96, gated.Count);
        Assert.All(gated, id => Assert.Equal(Catalog.Value.Get(id)!.Main.Condition_Value1, Catalog.Value.Get(id)!.GateQuest));
        Assert.Equal(new uint[] { GatedKhara }, Catalog.Value.GatedByQuest(GateQuest).Select(d => d.Id));
        
        Assert.Equal(new uint[] { 216, 217 }, Catalog.Value.GatedByQuest(111111380).Select(d => d.Id));
        Assert.Null(Catalog.Value.Get(24)!.GateQuest);
    }

    [Fact]
    public void AKill_BeforeTheQuest_DoesNotAcceptAGatedKhara()
    {
        
        var engine = Engine(new KharaStateDocument { Id = 1 }, quests: new HashSet<uint>());

        engine.OnNpcKilled(GatedNpc);

        Assert.Empty(engine.Frames);
        Assert.Null(engine.State.FindActive(GatedKhara));
    }

    [Fact]
    public void TheQuestsCompletion_AcceptsIt_WithABare2574_AndTheNextKillCountsOne()
    {
        
        
        var quests = new HashSet<uint> { GateQuest };
        var engine = Engine(new KharaStateDocument { Id = 1 }, quests);

        engine.OnQuestCompleted(GateQuest);

        Assert.Equal(new[] { $"2574:{Hex(KharaService.BuildAnsKharaAccept2574(GatedKhara, 0))}" }, Frames(engine));
        Assert.Equal(new uint[] { 0 }, engine.State.FindActive(GatedKhara)!.Counts);

        var next = Engine(engine.State, quests);
        next.OnNpcKilled(GatedNpc);
        Assert.Equal(new[] { $"2577:{Hex(KharaService.BuildNfyKharaInComplete2577(GatedKhara, 0, GatedNpc, 1))}" },
            Frames(next));
        Assert.Equal(new uint[] { 1 }, next.State.FindActive(GatedKhara)!.Counts);
    }

    [Fact]
    public void TwoKharasGatedOnOneQuest_AreAcceptedInIdOrder()
    {
        var engine = Engine(new KharaStateDocument { Id = 1 }, new HashSet<uint> { 111111380 });

        engine.OnQuestCompleted(111111380);

        Assert.Equal(new[]
        {
            $"2574:{Hex(KharaService.BuildAnsKharaAccept2574(216, 0))}",
            $"2574:{Hex(KharaService.BuildAnsKharaAccept2574(217, 0))}",
        }, Frames(engine));
    }

    [Fact]
    public void AGatedKhara_AlreadyHeldOrDone_IsNotAcceptedAgain()
    {
        var quests = new HashSet<uint> { GateQuest };
        var held = Engine(new KharaStateDocument
        {
            Id = 1,
            Active = new List<KharaActiveDocument>
            {
                new() { KharaId = GatedKhara, State = KharaService.StateInProgress, Counts = new List<uint> { 3 } },
            },
        }, quests);
        held.OnQuestCompleted(GateQuest);
        Assert.Empty(held.Frames);

        var done = Engine(new KharaStateDocument { Id = 1, Completed = new List<uint> { GatedKhara } }, quests);
        done.OnQuestCompleted(GateQuest);
        Assert.Empty(done.Frames);
    }

    [Fact]
    public void AnUngatedKillKhara_IsStillAcceptedByItsFirstKill_AndCreditedOne()
    {
        
        
        var engine = Engine(new KharaStateDocument { Id = 1 }, new HashSet<uint>());

        engine.OnNpcKilled(50070000);

        Assert.Equal(new[]
        {
            $"2574:{Hex(KharaService.BuildAnsKharaAccept2574(24, 0))}",
            $"2577:{Hex(KharaService.BuildNfyKharaInComplete2577(24, 0, 50070000, 1))}",
        }, Frames(engine));
    }

    private static KharaEngine Engine(KharaStateDocument state, HashSet<uint> quests, byte level = 50)
        => new(Catalog.Value, state, new KharaFacts { Level = level, IsQuestCompleted = quests.Contains });

    private static string[] Frames(KharaEngine engine) =>
        engine.Frames.Select(f => $"{f.Opcode}:{Hex(f.Body)}").ToArray();

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static List<KharaInfo> LoadRows()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Khara_Info.json")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Khara_Info.json");
        return JsonSerializer.Deserialize<List<KharaInfo>>(File.ReadAllText(Path.Combine(dir.FullName, "data", "Khara_Info.json")),
            DataJsonOptions)!;
    }
}
