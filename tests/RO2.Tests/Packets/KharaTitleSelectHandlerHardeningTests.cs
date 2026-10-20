using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// HSV CP-G9 / CP-G10: the 1438 Khara title select drops a body shorter than 8 B without touching the stored state or
/// answering, answers the 21751 refusal (and publishes / announces nothing) when the new state cannot be stored, and the
/// unreachable 1439 answers a NON-ZERO 2583 so a forged one cannot zero the client's title timer. The rules themselves
/// (Decide) are pinned by KharaTitleSelectReauditTests; these cover the handler's persist / publish / answer wiring.
/// </summary>
public sealed class KharaTitleSelectHandlerHardeningTests
{
    private const uint CharId = 0x7C09_0001;
    private const ushort Khara572 = 0x23C;   // grants title 206 (Khara_Info, see KharaTitleStore.MayWear)
    private const uint Title572 = 206;

    private sealed class FakeTitleRepo : IKharaTitleRepository
    {
        public KharaTitleDocument? Doc;
        public bool ThrowOnSave;
        public int Gets, Saves;
        public List<KharaTitleDocument> Saved { get; } = new();

        public Task<KharaTitleDocument?> GetAsync(uint characterId, CancellationToken ct = default)
        {
            Gets++;
            return Task.FromResult(Doc);
        }

        public Task SaveAsync(KharaTitleDocument doc, CancellationToken ct = default)
        {
            Saves++;
            if (ThrowOnSave) throw new InvalidOperationException("store down");
            Saved.Add(doc);
            Doc = doc;
            return Task.CompletedTask;
        }

        public Task StorePlayClockAsync(uint characterId, DateTime playClockUtc, DateTime updatedUtc, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeKharaState(params uint[] completed) : IKharaStateRepository
    {
        public Task<KharaStateDocument?> GetAsync(uint characterId, CancellationToken ct = default)
            => Task.FromResult<KharaStateDocument?>(new KharaStateDocument { Completed = [.. completed] });

        public Task SaveAsync(KharaStateDocument doc, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly KharaInfo[] Rows =
    {
        new() { Quest_ID = Khara572, Quest_Type = 1, NationEnable = 0, Reward_Title_Id = (byte)Title572 },
    };

    private sealed class Rig : IAsyncDisposable
    {
        public required Harness Net { get; init; }
        public required FakeTitleRepo Repo { get; init; }
        public required KharaTitleSelectHandler Handler { get; init; }
        public required KharaTitleSelectTimeResetHandler ResetHandler { get; init; }

        public static async Task<Rig> StartAsync(FakeTitleRepo repo)
        {
            var net = await Harness.StartAsync(1);
            var players = new PlayerStateManager(new NoDirtySink());
            var player = new PlayerState
            {
                SessionId = net.Sessions[0].SessionId, CharacterId = CharId, Name = "Hero", EntityId = 0x11u,
            };
            players.AddPlayer(player.SessionId, player);
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(net.Server);
            var store = new GameDataStore();
            typeof(GameDataStore).GetProperty(nameof(GameDataStore.KharaInfo))!.SetValue(store, Rows);
            var services = new ServiceCollection()
                .AddSingleton(new KharaTitleStore(repo))
                .AddSingleton<IKharaStateRepository>(new FakeKharaState(Khara572))
                .AddSingleton(store)
                .BuildServiceProvider();
            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            return new Rig
            {
                Net = net, Repo = repo,
                Handler = new KharaTitleSelectHandler(scopes, NullLogger<KharaTitleSelectHandler>.Instance, players, broadcast),
                ResetHandler = new KharaTitleSelectTimeResetHandler(scopes, NullLogger<KharaTitleSelectTimeResetHandler>.Instance, players, broadcast),
            };
        }

        public Task<(ushort Op, byte[] Body)> NextAsync() => Net.Readers[0].NextAsync();

        /// <summary>Asserts no frame is queued: a sentinel sent after the handler returned must be the next one read.</summary>
        public async Task AssertNothingQueuedAsync()
        {
            await Net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
            var (op, body) = await NextAsync();
            Assert.Equal(ZoneS2COpcodes.OnLevelUp, op);
            Assert.Equal(new byte[] { 0x5A }, body);
        }

        public ValueTask DisposeAsync() => Net.DisposeAsync();
    }

    private static byte[] Body1438(uint a, uint b)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, a);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), b);
        return body;
    }

    private static void ResetPublished() => KharaTitleStore.Publish(CharId, default, Rows);

    // ------------------------------------------------------------------ CP-G9 short payload

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    public async Task ShortBody_IsDropped_NoReadNoSaveNoAnswer(int length)
    {
        ResetPublished();
        try
        {
            // A worn title A with a running timer: reading the short body as [0][0] would have cleared it.
            var repo = new FakeTitleRepo { Doc = new KharaTitleDocument { Id = CharId, KharaA = Khara572, ATimerStartUtc = DateTime.UtcNow } };
            await using var rig = await Rig.StartAsync(repo);

            await rig.Handler.HandleAsync(rig.Net.Sessions[0], new byte[length]);

            Assert.Equal(0, repo.Gets);
            Assert.Equal(0, repo.Saves);
            Assert.Null(KharaTitleStore.PublishedFor(CharId));
            await rig.AssertNothingQueuedAsync();   // no 2582, no 2592, no 2498
        }
        finally { ResetPublished(); }
    }

    [Fact]
    public void TryParse1438_NeedsEightBytes()
    {
        Assert.False(KharaTitleSelectPackets.TryParse1438(new byte[7], out var a0, out var b0));
        Assert.Equal((0u, 0u), (a0, b0));
        Assert.True(KharaTitleSelectPackets.TryParse1438(Body1438(0x23C, 0x26B), out var a, out var b));
        Assert.Equal((0x23Cu, 0x26Bu), (a, b));
        Assert.True(KharaTitleSelectPackets.TryParse1438(new byte[12], out _, out _));   // longer bodies still parse
    }

    // ------------------------------------------------------------------ CP-G9 save failure

    [Fact]
    public async Task SaveFailure_AnswersRefusal_PublishesAndAnnouncesNothing()
    {
        ResetPublished();
        try
        {
            // Title A = Khara 572 worn, its timer started 30 s ago. [0][0] clears A: Decide accepts it, the store throws.
            var started = DateTime.UtcNow.AddSeconds(-30);
            var repo = new FakeTitleRepo
            {
                ThrowOnSave = true,
                Doc = new KharaTitleDocument { Id = CharId, KharaA = Khara572, ATimerStartUtc = started },
            };
            KharaTitleStore.Publish(CharId, KharaTitleState.From(repo.Doc), Rows);
            var before = KharaTitleStore.PublishedFor(CharId);
            Assert.Equal(Title572, before!.Value.TitleA);
            await using var rig = await Rig.StartAsync(repo);

            await rig.Handler.HandleAsync(rig.Net.Sessions[0], Body1438(0, 0));

            Assert.Equal(1, repo.Saves);
            Assert.Equal(before, KharaTitleStore.PublishedFor(CharId));   // nothing published
            var (op, body) = await rig.NextAsync();
            Assert.Equal(ZoneS2COpcodes.AnsKharaTitleSelect, op);          // 2582 first: no 2592 before it
            Assert.Equal(16, body.Length);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body));                 // A echo
            var timer = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
            Assert.InRange(timer, 85_000u, 90_000u);                                          // the stored state's A timer (120000 - 30 s)
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));        // B echo
            Assert.Equal(KharaTitleStore.ResultRefused, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
            await rig.AssertNothingQueuedAsync();                                             // no 2592 / 2498
        }
        finally { ResetPublished(); }
    }

    [Fact]
    public void SaveFailed_KeepsThePreviousState_WithItsTimer()
    {
        var t0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var prev = new KharaTitleState(Khara572, 0x26B, t0, t0);
        var d = KharaTitleStore.SaveFailed(prev, t0.AddSeconds(20));
        Assert.False(d.Accepted);
        Assert.Equal(KharaTitleStore.ResultRefused, d.Result);
        Assert.Equal(100_000u, d.Timer);
        Assert.Equal(prev, d.NewState);
        Assert.Equal(0u, KharaTitleStore.SaveFailed(default, t0).Timer);   // no timer running
    }

    [Fact]
    public async Task Accepted_StoresOnce_Publishes_AndAnswers2592Then2582()
    {
        ResetPublished();
        try
        {
            var repo = new FakeTitleRepo();
            await using var rig = await Rig.StartAsync(repo);

            await rig.Handler.HandleAsync(rig.Net.Sessions[0], Body1438(Khara572, 0));

            Assert.Equal(1, repo.Saves);
            Assert.Equal(Khara572, repo.Saved.Single().KharaA);
            Assert.Equal(Title572, KharaTitleStore.PublishedFor(CharId)!.Value.TitleA);
            var own = await rig.NextAsync();
            Assert.Equal(ZoneS2COpcodes.NfyKharaTitleChange, own.Op);
            var (op, body) = await rig.NextAsync();
            Assert.Equal(ZoneS2COpcodes.AnsKharaTitleSelect, op);
            Assert.Equal(KharaTitleStore.ResultOk, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
            Assert.Equal(KharaTitleStore.TitleTimerMs, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
            await rig.AssertNothingQueuedAsync();
        }
        finally { ResetPublished(); }
    }

    // ------------------------------------------------------------------ CP-G10 1439

    [Fact]
    public async Task TimeReset1439_AnswersNonZero_SoTheClientKeepsItsTimer()
    {
        await using var rig = await Rig.StartAsync(new FakeTitleRepo());

        await rig.ResetHandler.HandleAsync(rig.Net.Sessions[0], ReadOnlyMemory<byte>.Empty);

        var (op, body) = await rig.NextAsync();
        Assert.Equal(ZoneS2COpcodes.AnsKharaTitleSelectTimeRest, op);
        Assert.Equal(2, body.Length);
        Assert.NotEqual((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(body));   // 0 would zero the client's timer
        await rig.AssertNothingQueuedAsync();
    }
}
