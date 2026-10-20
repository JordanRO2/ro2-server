using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Khara auto-accept and condition rules for RMI 2574 and 2575 (Khara ids 6, 22, 26, 27 and 43).
/// </summary>
public class KharaAutoAcceptReauditTests
{
    
 /// <summary>First use of a "Used @1" item completes Khara 180: a 2577 reports count 1, followed by 2329.
 /// No 2502 is sent, since this Khara spends no points.</summary>
    [Fact]
    public async Task Khara_FirstItemUse_AutoAcceptsAndCountsOne()
    {
        var engine = await KharaEngineAsync(new KharaStateDocument(), level: 50);
        engine.OnCountedEvent(KharaService.CompleteUseItem, 21110002);
        Assert.Equal(new[] { "2574:b40000000000", "2577:b400000000f21c42010100" }, KharaFrames(engine));
    }

 /// <summary>A death (a fall) advances Khara 62 by one: a 2577 with subject 0 and count 1.</summary>
    [Fact]
    public async Task Khara_FirstDeath_AutoAccepts62AndCountsOne()
    {
        var engine = await KharaEngineAsync(new KharaStateDocument(), level: 20);
        engine.OnCountedEvent(KharaService.CompleteDeath, 0);
        Assert.Equal(new[] { "2574:3e0000000000", "2577:3e00000000000000000100" }, KharaFrames(engine));
    }

 /// <summary>The revival that reaches the target of Khara 63 (Value1 3) sends a 2577, then 2575 and 2576.</summary>
    [Fact]
    public async Task Khara_ThirdRevival_Completes63()
    {
        var state = new KharaStateDocument();
        state.Active.Add(new KharaActiveDocument { KharaId = 63, Counts = [2] });
        var engine = await KharaEngineAsync(state, level: 50);
        engine.OnCountedEvent(KharaService.CompleteRevived, 0);
        Assert.Equal(new[] { "2577:3f00000000000000000300", "2575:3f000000", "2576:0000000000000000" }, KharaFrames(engine));
        Assert.Contains(63u, engine.State.Completed);
    }

 /// <summary>
 /// A titled completion of Khara 178 (target 50) advances the titled-completion count, sent as a 2577 after the 2575 and 2576.
 /// The completion that reaches the target completes Khara 178, with no 2577 for that step.
 /// </summary>
    [Fact]
    public async Task Khara_TitledCompletion_Steps178_AndTheFiftiethCompletesIt()
    {
        var store = await LoadStoreAsync("Khara_Info.json");
        var catalog = KharaCatalog.For(store);
        var titled = store.KharaInfo.Select(r => (uint)r.Quest_ID).Distinct()
            .Where(id => id is not 501 and not 178 && catalog.Get(id)!.Main.Reward_Title_Id != 0).Order().ToList();
        uint quest501 = catalog.Get(501)!.Conditions[0].Complete_Type_Value1;

        var state = new KharaStateDocument { Completed = titled.Take(42).ToList() };
        state.Active.Add(new KharaActiveDocument { KharaId = 178, Counts = [42] });
        var engine = new KharaEngine(catalog, state, new KharaFacts { Level = 50, IsQuestCompleted = q => q == quest501 });
        engine.OnQuestCompleted(quest501);
        Assert.Equal(new[] { "2574:f50100000000", "2575:f5010000", "2576:0f00000000000000", "2577:b200000000000000002b00" },
            KharaFrames(engine));

        var full = new KharaStateDocument { Completed = titled.Take(49).ToList() };
        full.Active.Add(new KharaActiveDocument { KharaId = 178, Counts = [49] });
        var last = new KharaEngine(catalog, full, new KharaFacts { Level = 50, IsQuestCompleted = q => q == quest501 });
        last.OnQuestCompleted(quest501);
        var frames = KharaFrames(last);
        Assert.Equal(new[] { "2574:f50100000000", "2575:f5010000", "2576:0f00000000000000", "2575:b2000000" }, frames.Take(4));
        Assert.DoesNotContain(frames, f => f.StartsWith("2577:b2", StringComparison.Ordinal));
    }

 /// <summary>An "Achieved to join Guild" Khara completes on accept when the character is in a guild.
 /// Outside a guild it stays accepted until a join completes it.</summary>
    [Fact]
    public async Task Khara_GuildMember_Completes197()
    {
        var inGuild = await KharaEngineAsync(new KharaStateDocument(), level: 30, inGuild: true);
        Assert.True(inGuild.Accept(197));
        Assert.Equal(new[] { "2575:c5000000", "2576:0f00000000000000" }, KharaFrames(inGuild));

        var outside = await KharaEngineAsync(new KharaStateDocument(), level: 30, inGuild: false);
        Assert.True(outside.Accept(197));
        Assert.Empty(outside.Frames);
        var joined = new KharaEngine(KharaCatalog.For(await LoadStoreAsync("Khara_Info.json")), outside.State,
            new KharaFacts { Level = 30, InGuild = true });
        joined.OnGuildJoined();
        Assert.Equal(new[] { "2575:c5000000", "2576:0f00000000000000" }, KharaFrames(joined));
    }

 /// <summary>
 /// Khara 402 has Auto_Challenge 0 in its table rows, so a cast of skill group 6106 does not auto-accept it.
 /// Group 6106 belongs to Khara 413, which is limited to job 16.
 /// </summary>
    [Fact]
    public async Task Khara_NonRetailRows_DoNotAutoAccept402()
    {
        var store = await LoadStoreAsync("Khara_Info.json");
        var catalog = KharaCatalog.For(store);
        Assert.False(catalog.Get(402)!.AutoChallenge);
        Assert.DoesNotContain(catalog.Get(402)!.Conditions, c => c.Complete_Type == KharaService.CompleteUseSkill);
        var engine = new KharaEngine(catalog, new KharaStateDocument(), new KharaFacts { Level = 50, CombatClass = 16 });
        engine.AutoAcceptOn(KharaService.CompleteUseSkill, 6106);
        Assert.DoesNotContain(engine.Frames, f => f.Opcode == ZoneS2COpcodes.AnsKharaAccept
            && BinaryPrimitives.ReadUInt32LittleEndian(f.Body) == 402);
    }

 /// <summary>A profession-bound Auto_Challenge Khara (187, PC_ProJob 1) starts only for characters of that profession.</summary>
    [Fact]
    public async Task Khara_ProfessionBoundAutoKhara_StartsOnlyForItsProfession()
    {
        var store = await LoadStoreAsync("Khara_Info.json");
        var catalog = KharaCatalog.For(store);
        var def = catalog.Get(187)!;
        Assert.Equal(1, def.Main.PC_ProJob);
        uint item = def.Conditions.First(c => c.Complete_Type == KharaService.CompleteHoldItem).Complete_Type_Value1;

        var other = new KharaEngine(catalog, new KharaStateDocument(),
            new KharaFacts { Level = 50, Profession = 2, HeldCount = id => id == item ? 1 : 0 });
        other.OnHeldCountChanged(item);
        Assert.DoesNotContain(other.Frames, f => f.Opcode == ZoneS2COpcodes.AnsKharaAccept);

        var own = new KharaEngine(catalog, new KharaStateDocument(),
            new KharaFacts { Level = 50, Profession = 1, HeldCount = id => id == item ? 1 : 0 });
        own.OnHeldCountChanged(item);
        Assert.Equal("2574:bb0000000000", KharaFrames(own)[0]);
    }

    private static async Task<KharaEngine> KharaEngineAsync(KharaStateDocument state, byte level, bool inGuild = false)
    {
        var store = await LoadStoreAsync("Khara_Info.json");
        return new KharaEngine(KharaCatalog.For(store), state, new KharaFacts { Level = level, InGuild = inGuild });
    }

    private static string[] KharaFrames(KharaEngine engine) =>
        engine.Frames.Select(f => $"{f.Opcode}:{Hex(f.Body)}").ToArray();
}
