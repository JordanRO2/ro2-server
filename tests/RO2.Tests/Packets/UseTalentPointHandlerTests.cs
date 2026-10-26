using System.Buffers.Binary;
using RO2.Skills.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// CP-G4..G7: the 1310 ReqPCUseTalentPoint handler. Every decision sits in a pure helper (<c>TryParse</c>,
/// <c>DedupKeepLast</c>, <c>TalentRules.IsTalentAllocationLegal</c>); the loopback tests pin the two outcomes:
/// refused -> nothing persisted, nothing applied, no 2543; accepted -> exactly one batch, the mirror applied, one 2543.
/// Job 6 (Swordsman) rows of the shipped Talent_Table: 1102 (no parent), 1117 (needs 1102), 1108 (needs 1117), 1110.
/// </summary>
public sealed class UseTalentPointHandlerTests
{
    private const uint CharId = 777001u;
    private const uint Self = 0x0000_0011u;

    /// <summary>A 1310 body: [u8 marker][u8 len][u16 selfLen][u16 msgTag][u32 self handle][u8 cost][u32 count] then
    /// {u32 id, u8 lvl} x elements.</summary>
    private static byte[] Build1310(IReadOnlyList<(uint Id, byte Level)> elements, uint? declaredCount = null,
        ushort msgTag = UseTalentPointHandler.MsgTag, uint handle = Self, byte cost = 5)
    {
        var body = new byte[15 + 5 * elements.Count];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)(body.Length - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), msgTag);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), handle);
        body[10] = cost;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(11), declaredCount ?? (uint)elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(15 + 5 * i), elements[i].Id);
            body[19 + 5 * i] = elements[i].Level;
        }
        return body;
    }

    // ---- pure helpers ---------------------------------------------------------------------------------------

    [Fact]
    public void TryParse_ReadsAWellFormedRequest()
    {
        var body = Build1310([(1102u, (byte)2), (1117u, (byte)1)]);

        Assert.Equal(UseTalentPointHandler.RequestError.None,
            UseTalentPointHandler.TryParse(body, Self, out var cost, out var count, out var all));
        Assert.Equal((byte)5, cost);
        Assert.Equal(2u, count);
        Assert.Equal(new[] { (1102u, (byte)2), (1117u, (byte)1) }, all);
    }

    [Fact]
    public void TryParse_RefusesAWrongMsgTag_AWrongHandle_AndAShortBody()
    {
        Assert.Equal(UseTalentPointHandler.RequestError.BadMsgTag,
            UseTalentPointHandler.TryParse(Build1310([(1102u, (byte)1)], msgTag: 58381), Self, out _, out _, out _));
        Assert.Equal(UseTalentPointHandler.RequestError.HandleMismatch,
            UseTalentPointHandler.TryParse(Build1310([(1102u, (byte)1)], handle: 0x0000_0111u), Self, out _, out _, out _));
        Assert.Equal(UseTalentPointHandler.RequestError.TooShort,
            UseTalentPointHandler.TryParse(new byte[19], Self, out _, out _, out _));
    }

    [Fact]
    public void TryParse_RefusesACountAbove230_BeforeReadingAnyElement()
    {
        // count = 0xFFFFFFFF with 4000 elements behind it
        var big = Enumerable.Range(0, 4000).Select(i => (1100u + (uint)i, (byte)1)).ToList();
        Assert.Equal(UseTalentPointHandler.RequestError.TooManyElements,
            UseTalentPointHandler.TryParse(Build1310(big, declaredCount: 0xFFFFFFFFu), Self, out _, out _, out var all));
        Assert.Empty(all);
        // 231 declared and present is over the cap too; 230 is the most
        Assert.Equal(UseTalentPointHandler.RequestError.TooManyElements,
            UseTalentPointHandler.TryParse(Build1310(big.Take(231).ToList()), Self, out _, out _, out _));
        Assert.Equal(UseTalentPointHandler.RequestError.None,
            UseTalentPointHandler.TryParse(Build1310(big.Take(230).ToList()), Self, out _, out _, out _));
    }

    [Fact]
    public void TryParse_RefusesACountLargerThanTheElementsPresent()
    {
        Assert.Equal(UseTalentPointHandler.RequestError.Truncated,
            UseTalentPointHandler.TryParse(Build1310([(1102u, (byte)1)], declaredCount: 3), Self, out _, out _, out _));
    }

    [Fact]
    public void DedupKeepLast_KeepsTheLastLevelOfAGroup_AndDropsZeroEntries()
    {
        var result = UseTalentPointHandler.DedupKeepLast(
            [(1102u, (byte)2), (0u, (byte)5), (1117u, (byte)1), (1102u, (byte)4), (1108u, (byte)0)]);

        Assert.Equal(new[] { (1117u, (byte)1), (1102u, (byte)4) }, result);
    }

    // ---- handler, over a loopback session -------------------------------------------------------------------

    private sealed class FakeSkills : ISkillRepository
    {
        public List<IReadOnlyList<(uint SkillId, byte Level)>> Batches { get; } = [];
        public int OtherWrites;
        public bool Throw;

        public Task<IReadOnlyList<CharacterSkill>> GetSkillsAsync(uint characterId)
            => Task.FromResult<IReadOnlyList<CharacterSkill>>([]);
        public Task LearnSkillAsync(uint characterId, uint skillId, byte level) { OtherWrites++; return Task.CompletedTask; }
        public Task UpdateSkillLevelAsync(uint characterId, uint skillId, byte newLevel) { OtherWrites++; return Task.CompletedTask; }
        public Task DeleteSkillsAsync(uint characterId, IReadOnlyCollection<uint> skillIds, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpsertSkillsAsync(uint characterId, IReadOnlyList<(uint SkillId, byte Level)> skills, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("mongo down");
            Assert.Equal(CharId, characterId);
            Batches.Add(skills.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required Harness Net { get; init; }
        public required PlayerState Player { get; init; }
        public required FakeSkills Skills { get; init; }
        public required UseTalentPointHandler Handler { get; init; }
        public required GameDataStore Store { get; init; }

        public static async Task<Rig> StartAsync(Action<PlayerState>? tweak = null)
        {
            var net = await Harness.StartAsync(1);
            var players = new PlayerStateManager(new NoDirtySink());
            var player = new PlayerState { SessionId = net.Sessions[0].SessionId, CharacterId = CharId, Name = "Spender",
                EntityId = Self, Job = 6, Level = 50 };
            tweak?.Invoke(player);
            players.AddPlayer(player.SessionId, player);
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(net.Server);
            var store = await LoadStoreAsync("Talent_Table.json");
            var skills = new FakeSkills();
            var services = new ServiceCollection().AddSingleton(store).AddScoped<ISkillRepository>(_ => skills)
                .BuildServiceProvider();
            var handler = new UseTalentPointHandler(services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<UseTalentPointHandler>.Instance, players, broadcast);
            return new Rig { Net = net, Player = player, Skills = skills, Handler = handler, Store = store };
        }

        public Task SendAsync(byte[] body) => Handler.HandleAsync(Net.Sessions[0], body);

        /// <summary>Sends a marker frame; reading it back first proves nothing else was queued before it.</summary>
        public async Task AssertNoReplyAsync()
        {
            await Net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
            Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await Net.Readers[0].NextAsync()));
        }

        public ValueTask DisposeAsync() => Net.DisposeAsync();
    }

    [Fact]
    public async Task Accepted_WritesOneBatch_MirrorsIt_AndAcks()
    {
        await using var rig = await Rig.StartAsync();
        List<(uint, byte)> sent = [(1102u, (byte)1), (1117u, (byte)1), (1108u, (byte)1)];

        await rig.SendAsync(Build1310(sent));

        var (op, body) = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnUseTalentPoint, op);
        var batch = Assert.Single(rig.Skills.Batches);
        Assert.Equal(sent, batch.Select(b => (b.SkillId, b.Level)));
        Assert.Equal(0, rig.Skills.OtherWrites);
        Assert.Equal(new Dictionary<uint, byte> { [1102] = 1, [1117] = 1, [1108] = 1 }, rig.Player.LearnedSkills);

        var tree = rig.Player.LearnedSkills.Select(kv => (kv.Key, kv.Value));
        var remaining = TalentRules.TalentPointsRemaining(rig.Store, 6, 50, tree);
        Assert.Equal(QuestProgressPackets.BuildUseTalentPoint2543(sent.Select(s => (s.Item1, s.Item2)).ToList(), remaining), body);
    }

    [Fact]
    public async Task DuplicateGroups_BecomeOneUpsertAtTheLastLevel()
    {
        await using var rig = await Rig.StartAsync();

        await rig.SendAsync(Build1310([(1102u, (byte)2), (1102u, (byte)4), (1102u, (byte)5)]));

        Assert.Equal(ZoneS2COpcodes.OnUseTalentPoint, (await rig.Net.Readers[0].NextAsync()).Op);
        var batch = Assert.Single(rig.Skills.Batches);
        Assert.Equal((1102u, (byte)5), Assert.Single(batch));
        Assert.Equal((byte)5, rig.Player.LearnedSkills[1102]);
    }

    [Fact]
    public async Task OversizedCount_PersistsNothingAndSendsNoAck()
    {
        await using var rig = await Rig.StartAsync();
        var big = Enumerable.Range(0, 4000).Select(i => (1102u, (byte)1)).ToList();

        await rig.SendAsync(Build1310(big, declaredCount: 0xFFFFFFFFu));
        await rig.SendAsync(Build1310(big));

        Assert.Empty(rig.Skills.Batches);
        Assert.Empty(rig.Player.LearnedSkills);
        await rig.AssertNoReplyAsync();
    }

    [Fact]
    public async Task WrongMsgTag_OrForeignHandle_PersistsNothingAndSendsNoAck()
    {
        await using var rig = await Rig.StartAsync();

        await rig.SendAsync(Build1310([(1102u, (byte)1)], msgTag: 58381));
        await rig.SendAsync(Build1310([(1102u, (byte)1)], handle: 0x0000_0511u));

        Assert.Empty(rig.Skills.Batches);
        Assert.Empty(rig.Player.LearnedSkills);
        await rig.AssertNoReplyAsync();
    }

    [Fact]
    public async Task ChildWithoutParent_IsRefused_NothingPersisted()
    {
        await using var rig = await Rig.StartAsync();

        await rig.SendAsync(Build1310([(1108u, (byte)1)]));

        Assert.Empty(rig.Skills.Batches);
        Assert.Empty(rig.Player.LearnedSkills);
        await rig.AssertNoReplyAsync();
    }

    [Fact]
    public async Task OneIllegalEntry_RefusesTheWholeBatch()
    {
        await using var rig = await Rig.StartAsync();

        // 1102 alone is fine; 6301 is a job-18 talent. All or nothing.
        await rig.SendAsync(Build1310([(1102u, (byte)1), (6301u, (byte)1)]));

        Assert.Empty(rig.Skills.Batches);
        Assert.Empty(rig.Player.LearnedSkills);
        await rig.AssertNoReplyAsync();
    }

    [Fact]
    public async Task LoweringAHeldLevel_IsRefused_AndTheLevelStays()
    {
        await using var rig = await Rig.StartAsync(p => p.LearnedSkills[1110] = 3);

        await rig.SendAsync(Build1310([(1110u, (byte)1)]));

        Assert.Empty(rig.Skills.Batches);
        Assert.Equal((byte)3, rig.Player.LearnedSkills[1110]);
        await rig.AssertNoReplyAsync();
    }

    [Fact]
    public async Task RepositoryThrows_LeavesLearnedSkillsUnchanged_AndSendsNoAck()
    {
        await using var rig = await Rig.StartAsync(p => p.LearnedSkills[1110] = 1);
        rig.Skills.Throw = true;
        var before = rig.Player.LearnedSkills;

        await rig.SendAsync(Build1310([(1102u, (byte)1), (1117u, (byte)1)]));

        Assert.Empty(rig.Skills.Batches);
        Assert.Same(before, rig.Player.LearnedSkills);
        Assert.Equal(new Dictionary<uint, byte> { [1110] = 1 }, rig.Player.LearnedSkills);
        await rig.AssertNoReplyAsync();
    }

    [Fact]
    public void OverlayLearnedSkills_SwapsTheDictionary_SoAReaderNeverSeesItGrow()
    {
        var player = new PlayerState();
        player.LearnedSkills[1110] = 1;
        var held = player.LearnedSkills;

        player.OverlayLearnedSkills([(1102u, (byte)2), (1110u, (byte)3)]);

        Assert.Equal(new Dictionary<uint, byte> { [1110] = 1 }, held);   // the reader's copy is untouched
        Assert.Equal(new Dictionary<uint, byte> { [1110] = 3, [1102] = 2 }, player.LearnedSkills);
    }
}
