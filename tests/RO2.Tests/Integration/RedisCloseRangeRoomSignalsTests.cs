using System.Collections.Concurrent;
using RO2.Domain.Entities;
using RO2.Persistence.Redis;
using Testcontainers.Redis;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Ephemeral Redis 7 container for <see cref="RedisCloseRangeRoomSignalsTests"/>, started through IClassFixture.
/// When Docker is unavailable, <see cref="Available"/> is false and the tests are skipped.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7").Build();

    public RedisConnection? Connection { get; private set; }

    public bool Available => Connection is not null;

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            var connection = new RedisConnection(_container.GetConnectionString());
            _ = connection.Db; 
            Connection = connection;
        }
        catch
        {
            Connection = null; 
        }
    }

    public async Task DisposeAsync()
    {
        if (Connection is not null) await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}

public sealed class RedisCloseRangeRoomSignalsTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fx;

    public RedisCloseRangeRoomSignalsTests(RedisFixture fx) => _fx = fx;

    private RedisCloseRangeRoomSignalBus Bus() => new(_fx.Connection!);

    private static byte[] NewRoom() => Guid.NewGuid().ToByteArray();

    [Fact]
    public async Task Open_then_FindOpenRoom_returns_owner_capacity_and_password()
    {
        if (!_fx.Available) return;
        var bus = Bus();
        var room = NewRoom();
        byte[] pw = [0x31, 0x00, 0x32, 0x00];

        await bus.PublishOpenAsync(room, 7001, 10, pw);

        var open = await bus.FindOpenRoomAsync(room);
        Assert.NotNull(open);
        Assert.Equal(CloseRangeRoomSignals.OpenKind, open.Kind);
        Assert.Equal(room, open.RoomGuid);
        Assert.Equal(7001u, open.CharacterId);
        Assert.Equal((byte)10, open.Capacity);
        Assert.Equal(pw, open.Password);
        Assert.NotEqual(default, open.CreatedUtc);
        Assert.Null(await bus.FindOpenRoomAsync(NewRoom()));
        Assert.Null(await bus.GetRoomHistoryAsync(NewRoom()));
    }

    [Fact]
    public async Task Joins_and_exits_appear_in_room_history_in_order()
    {
        if (!_fx.Available) return;
        var bus = Bus();
        var room = NewRoom();
        var other = NewRoom();

        await bus.PublishOpenAsync(room, 10, 10, default);
        await bus.PublishJoinAsync(room, 11, 2);
        await bus.PublishJoinAsync(other, 99, 2); 
        await bus.PublishJoinAsync(room, 12, 3);
        await bus.PublishExitAsync(room, 11, 2, 0, false);

        var history = await bus.GetRoomHistoryAsync(room);
        Assert.NotNull(history);
        Assert.Equal(10u, history.Open.CharacterId);
        Assert.Collection(history.Later,
            s => { Assert.Equal(CloseRangeRoomSignals.JoinKind, s.Kind); Assert.Equal(11u, s.CharacterId); Assert.Equal(2, s.MemberCount); },
            s => { Assert.Equal(CloseRangeRoomSignals.JoinKind, s.Kind); Assert.Equal(12u, s.CharacterId); Assert.Equal(3, s.MemberCount); },
            s => { Assert.Equal(CloseRangeRoomSignals.ExitKind, s.Kind); Assert.Equal(11u, s.CharacterId); Assert.Equal(2, s.MemberCount); });
        Assert.All(history.Later, s => Assert.Equal(room, s.RoomGuid));
        Assert.True(history.Later.Zip(history.Later.Skip(1), (a, b) => string.CompareOrdinal(a.Id, b.Id) < 0).All(x => x));
        Assert.True(string.CompareOrdinal(history.Open.Id, history.Later[0].Id) < 0);
    }

    [Fact]
    public async Task Owner_exit_without_new_master_closes_the_room()
    {
        if (!_fx.Available) return;
        var bus = Bus();
        var room = NewRoom();

        await bus.PublishOpenAsync(room, 20, 10, default);
        Assert.NotNull(await bus.FindOpenRoomAsync(room));

        await bus.PublishExitAsync(room, 20, 0, 0, ownerGone: true);

        Assert.Null(await bus.FindOpenRoomAsync(room));
        var history = await bus.GetRoomHistoryAsync(room);
        Assert.NotNull(history); 
        var exit = Assert.Single(history.Later);
        Assert.True(exit.OwnerGone);
    }

    [Fact]
    public async Task Master_hand_off_makes_FindOpenRoom_report_the_new_owner()
    {
        if (!_fx.Available) return;
        var bus = Bus();
        var room = NewRoom();

        await bus.PublishOpenAsync(room, 30, 10, default);
        await bus.PublishJoinAsync(room, 31, 2);
        await bus.PublishExitAsync(room, 30, 1, newMasterCharacterId: 31, ownerGone: false);

        var open = await bus.FindOpenRoomAsync(room);
        Assert.NotNull(open);
        Assert.Equal(31u, open.CharacterId);

        
        await bus.PublishExitAsync(room, 30, 0, 0, false);
        Assert.Equal(31u, (await bus.FindOpenRoomAsync(room))!.CharacterId);
        await bus.PublishExitAsync(room, 31, 0, 0, false);
        Assert.Null(await bus.FindOpenRoomAsync(room));
    }

    [Fact]
    public async Task Subscribe_delivers_signals_in_order_and_a_late_subscriber_replays()
    {
        if (!_fx.Available) return;
        var bus = Bus();
        var room = NewRoom();
        var from = DateTime.UtcNow.AddHours(-1);

        await bus.PublishOpenAsync(room, 40, 10, default);
        await bus.PublishJoinAsync(room, 41, 2);

        var live = new ConcurrentQueue<CloseRangeRoomSignal>();
        var replay = new ConcurrentQueue<CloseRangeRoomSignal>();
        var errors = new ConcurrentQueue<Exception>();
        using var cts = new CancellationTokenSource();

        Task Collect(ConcurrentQueue<CloseRangeRoomSignal> into, CloseRangeRoomSignal s)
        {
            if (s.RoomGuid.AsSpan().SequenceEqual(room)) into.Enqueue(s);
            return Task.CompletedTask;
        }

        
        var liveTask = bus.SubscribeAsync(from, s => Collect(live, s), errors.Enqueue, cts.Token);
        await WaitForAsync(() => live.Count >= 2);

        await bus.PublishExitAsync(room, 41, 1, 0, false);
        await bus.PublishJoinAsync(room, 42, 2);
        await WaitForAsync(() => live.Count >= 4);

        
        var replayTask = bus.SubscribeAsync(from, s => Collect(replay, s), errors.Enqueue, cts.Token);
        await WaitForAsync(() => replay.Count >= 4);

        await cts.CancelAsync();
        await Task.WhenAll(liveTask, replayTask);

        Assert.Empty(errors);
        string[] kinds = [CloseRangeRoomSignals.OpenKind, CloseRangeRoomSignals.JoinKind, CloseRangeRoomSignals.ExitKind, CloseRangeRoomSignals.JoinKind];
        uint[] characters = [40, 41, 41, 42];
        foreach (var delivered in new[] { live, replay })
        {
            var signals = delivered.ToArray();
            Assert.Equal(kinds, signals.Select(s => s.Kind));
            Assert.Equal(characters, signals.Select(s => s.CharacterId));
        }
    }

    [Fact]
    public async Task Subscribe_does_not_redeliver_a_signal_whose_handler_threw()
    {
        if (!_fx.Available) return;
        var bus = Bus();
        var room = NewRoom();
        var from = DateTime.UtcNow.AddHours(-1);
        var seen = new ConcurrentQueue<uint>();
        var errors = new ConcurrentQueue<Exception>();
        using var cts = new CancellationTokenSource();

        await bus.PublishOpenAsync(room, 50, 10, default);
        await bus.PublishJoinAsync(room, 51, 2);

        var task = bus.SubscribeAsync(from, s =>
        {
            if (!s.RoomGuid.AsSpan().SequenceEqual(room)) return Task.CompletedTask;
            seen.Enqueue(s.CharacterId);
            return s.CharacterId == 50 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }, errors.Enqueue, cts.Token);

        await WaitForAsync(() => seen.Count >= 2);
        await Task.Delay(RedisCloseRangeRoomSignalBus.RetryDelay * 2);
        await cts.CancelAsync();
        await task;

        Assert.Equal<uint>([50, 51], seen.ToArray());
        Assert.Single(errors, e => e is InvalidOperationException);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the subscription");
            await Task.Delay(50);
        }
    }
}
