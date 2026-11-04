using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// [35] The pyramid state is saved before the entry is taken or the reward is paid. A save that throws leaves the held
/// reward, tells the client it failed, and a later finish settles it once the store is back.
/// </summary>
public class IllusionPyramidSaveFailureTests
{
    private sealed class FlakyRepo : IIllusionPyramidStateRepository
    {
        public IllusionPyramidStateDocument Stored { get; private set; } = new() { Id = 1, Mystical = 20, Bright = 0, TopRecord = 1 };
        public bool FailSave { get; set; }

        public Task<IllusionPyramidStateDocument> GetAsync(uint characterId, CancellationToken ct = default)
            => Task.FromResult(new IllusionPyramidStateDocument
            { Id = Stored.Id, Mystical = Stored.Mystical, Bright = Stored.Bright, TopRecord = Stored.TopRecord });

        public Task SaveAsync(IllusionPyramidStateDocument doc, CancellationToken ct = default)
        {
            if (FailSave) throw new IOException("store down");
            Stored = doc;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AFailedSaveAtTheResult_KeepsTheHeldReward_AndAFinishSettlesItLater()
    {
        await using var net = await Harness.StartAsync(1);
        var session = net.Sessions[0];
        var player = new PlayerState { SessionId = session.SessionId, EntityId = 0x09491D11, CharacterId = 1, Name = "Pyra" };
        var repo = new FlakyRepo { FailSave = true };
        await using var services = new ServiceCollection().AddSingleton<IIllusionPyramidStateRepository>(repo).BuildServiceProvider();
        var reward = new IllusionPyramidCatalog.Reward(30000267, 7, 1, 3, IllusionPyramidCatalog.MysticalMileageId, true, false);
        var slot = IllusionPyramidRunStore.For(player);
        slot.Run = new IllusionPyramidRunStore.Run { Entry = IllusionPyramidCatalog.EntryKind.Mystical, Held = reward, Floor = 7 };
        slot.DrewSinceFinish = true;

        await IllusionPyramidService.GetResultAsync(session, player, services, NullLogger.Instance, CancellationToken.None);

        var (op, body) = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.AnsIllusionPyramidGetResult, op);
        Assert.Equal(IllusionPyramidService.ResultSystemError, BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.NotNull(slot.Run?.Held);                       // the reward is not dropped
        Assert.Equal(20, repo.Stored.Mystical);               // nothing taken, nothing paid

        repo.FailSave = false;
        await IllusionPyramidService.FinishAsync(session, player, services, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(20 - IllusionPyramidCatalog.MileageEntryCost + 3, repo.Stored.Mystical);
        Assert.Equal(7, repo.Stored.TopRecord);
        Assert.Null(slot.Run);
    }

    [Fact]
    public async Task AGoodSaveAtTheResult_PaysTheMileageOnce()
    {
        await using var net = await Harness.StartAsync(1);
        var session = net.Sessions[0];
        var player = new PlayerState { SessionId = session.SessionId, EntityId = 0x09491D12, CharacterId = 2, Name = "Pyra2" };
        var repo = new FlakyRepo();
        await using var services = new ServiceCollection().AddSingleton<IIllusionPyramidStateRepository>(repo).BuildServiceProvider();
        var reward = new IllusionPyramidCatalog.Reward(30000267, 7, 1, 3, IllusionPyramidCatalog.MysticalMileageId, true, false);
        var slot = IllusionPyramidRunStore.For(player);
        slot.Run = new IllusionPyramidRunStore.Run { Entry = IllusionPyramidCatalog.EntryKind.Mystical, Held = reward, Floor = 7 };
        slot.DrewSinceFinish = true;

        await IllusionPyramidService.GetResultAsync(session, player, services, NullLogger.Instance, CancellationToken.None);

        var (op, body) = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.AnsIllusionPyramidGetResult, op);
        Assert.Equal(IllusionPyramidService.Build2908(reward), body);
        Assert.Equal(20 - IllusionPyramidCatalog.MileageEntryCost + 3, repo.Stored.Mystical);
        Assert.Equal(7, repo.Stored.TopRecord);
        Assert.Null(slot.Run?.Held);
    }
}
