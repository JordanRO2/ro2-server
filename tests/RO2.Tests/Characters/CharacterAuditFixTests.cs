using System.Reflection;
using RO2.Characters.Application;
using RO2.Characters.Domain;
using RO2.GameData;
using Xunit;

namespace RO2.Tests.Characters;

/// <summary>Regression pins for the 2026-11-03 Character audit fixes ([10] job exp, [11] name regex, [32] allow-review).</summary>
public class CharacterAuditFixTests
{
    private class Recorder : DispatchProxy
    {
        public readonly List<string> Calls = [];
        public bool Throw;
        public byte CreatedRace;

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            Calls.Add(m!.Name);
            if (m.Name == nameof(ICharacterRepository.CreateCharacterAsync))
            {
                CreatedRace = args!.Length > 6 ? (byte)args[6]! : (byte)0;
                return Task.FromResult<(Character?, string?)>((new Character { Id = 7, Race = CreatedRace }, null));
            }
            return Throw ? Task.FromException(new IOException("db down")) : Task.CompletedTask;
        }
    }

    private static (ICharacterRepository repo, Recorder rec) NewRepo(bool fail = false)
    {
        var repo = DispatchProxy.Create<ICharacterRepository, Recorder>();
        var rec = (Recorder)(object)repo;
        rec.Throw = fail;
        return (repo, rec);
    }

    // ---- [11] ----

    [Theory]
    [InlineData("Abc\n")]
    [InlineData("Abc\r\n")]
    [InlineData("\nAbc")]
    public void Name_WithNewline_IsAFormatRefusal(string name)
        => Assert.Equal(NameRefusal.Format, NameRules.Check(name, []).refusal);

    [Fact]
    public void Name_Plain_IsAccepted() => Assert.Equal(NameRefusal.None, NameRules.Check("Abc_def1", []).refusal);

    // ---- [32] ----

    private sealed class Gateway : IPlayerSessionGateway
    {
        public Character? Snapshot { get; init; }
        public ushort BindIndex { get; set; }
        public string Name { get; set; } = "";
        public byte Gender { set { } }
    }

    [Fact]
    public async Task AllowAnyoneReview_AnnounceThrows_StillPersists_AndTheAnnounceErrorPropagates()
    {
        var (repo, rec) = NewRepo();
        var gw = new Gateway { Snapshot = new Character() };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetAllowAnyoneReviewFlow.ExecuteAsync(repo, gw, 7, true, () => throw new InvalidOperationException("session closed")));

        Assert.Equal([nameof(ICharacterRepository.UpdateAllowAnyoneReviewAsync)], rec.Calls);
        Assert.True(gw.Snapshot!.AllowAnyoneReview);
    }

    [Fact]
    public async Task AllowAnyoneReview_WriteFails_ReturnsTheError()
    {
        var (repo, _) = NewRepo(fail: true);
        var ex = await SetAllowAnyoneReviewFlow.ExecuteAsync(repo, new Gateway(), 7, true, () => Task.CompletedTask);
        Assert.IsType<IOException>(ex);
    }

    // ---- [10] ----

    private sealed class Data : ICharacterGameData
    {
        public IReadOnlyDictionary<byte, ExpTableEntry> ExpTable { get; } = new Dictionary<byte, ExpTableEntry>
        {
            [1] = new() { Base_Level = 1, Base_Exp_Next = 100 },
            [2] = new() { Base_Level = 2, Base_Exp_Next = 100 },
        };
        public bool UnionExists(uint unionId) => throw new NotSupportedException();
        public (int str, int agi, int intel, int wis, int vit, int luk)? ClassBasePrimaries(byte job) => throw new NotSupportedException();
        public int BonusStatCost(int currentStatValue) => throw new NotSupportedException();
        public bool IsAppearanceTicket(uint itemId) => throw new NotSupportedException();
        public byte RequireSex(uint itemId) => throw new NotSupportedException();
        public bool IsNameChangeScroll(uint itemId) => throw new NotSupportedException();
        public (int hp, int mp, int str, int agi, int intel, int wis, int vit, int luk)? SeedStats(byte job) => null;
        public ulong MasteryMaxPointBalance(byte masteryLevel) => masteryLevel == 0 ? 0UL : 10_000_000UL;
    }

    private sealed class Notifier : IJobExpNotifier
    {
        public Task AnnounceAsync(JobExpRules.Result result, ulong gain) => Task.CompletedTask;
        public void PersistFailed(Exception ex, uint characterId, JobExpRules.Result result) { }
        public Task OnLevelUpAsync() => Task.CompletedTask;
        public Task MasteryPointsGainedAsync(long delta, ulong total) => Task.CompletedTask;
    }

    [Fact]
    public async Task JobExpGrant_WritesOnlyThePair_NoReadNoWholeRowSave()
    {
        var (repo, rec) = NewRepo();
        var c = new Character { Id = 5, JobLevel = 1, JobExp = 10 };

        Assert.True(await JobExpGrantFlow.GrantAsync(c, 20, new Data(), repo, new Notifier()));

        Assert.Equal([nameof(ICharacterRepository.UpdateJobLevelExpAsync)], rec.Calls);
        Assert.Equal((1, 30UL), (c.JobLevel, c.JobExp));
    }

    private sealed class ThrowingAnnounce : IJobExpNotifier
    {
        public Task AnnounceAsync(JobExpRules.Result result, ulong gain) => Task.FromException(new IOException("session dropped"));
        public void PersistFailed(Exception ex, uint characterId, JobExpRules.Result result) { }
        public Task OnLevelUpAsync() => Task.CompletedTask;
        public Task MasteryPointsGainedAsync(long delta, ulong total) => Task.CompletedTask;
    }

    [Fact]
    public async Task JobExpAtCap_ConvertsToMasteryPoints_WhenStarted()
    {
        var (repo, rec) = NewRepo();
        var c = new Character { Id = 7, JobLevel = 2, JobExp = 0, MasteryLevel = 1, JobMasteryPoints = 100 };

        Assert.True(await JobExpGrantFlow.GrantAsync(c, 5000, new Data(), repo, new Notifier()));

        Assert.Equal(5100UL, c.JobMasteryPoints);                 // profession exp added to the balance one for one
        Assert.Equal([nameof(ICharacterRepository.UpdateJobMasteryAsync)], rec.Calls);
    }

    [Fact]
    public async Task JobExpAtCap_IsDropped_WhenMasteryNotStarted()
    {
        var (repo, rec) = NewRepo();
        var c = new Character { Id = 8, JobLevel = 2, JobExp = 0, MasteryLevel = 0, JobMasteryPoints = 100 };

        Assert.False(await JobExpGrantFlow.GrantAsync(c, 5000, new Data(), repo, new Notifier()));

        Assert.Equal(100UL, c.JobMasteryPoints);                 // unchanged, and nothing persisted
        Assert.Empty(rec.Calls);
    }

    [Fact]
    public async Task JobExpAtCap_StopsAtThePointCap()
    {
        var (repo, _) = NewRepo();
        var c = new Character { Id = 9, JobLevel = 2, JobExp = 0, MasteryLevel = 1, JobMasteryPoints = 9_999_000 };

        Assert.True(await JobExpGrantFlow.GrantAsync(c, 5000, new Data(), repo, new Notifier()));

        Assert.Equal(10_000_000UL, c.JobMasteryPoints);          // clamped to the cap, not 10,004,000
    }

    [Fact]
    public async Task JobExpGrant_PersistsThePairEvenWhenTheAnnounceThrows()
    {
        var (repo, rec) = NewRepo();
        var c = new Character { Id = 5, JobLevel = 1, JobExp = 10 };

        await Assert.ThrowsAsync<IOException>(() => JobExpGrantFlow.GrantAsync(c, 20, new Data(), repo, new ThrowingAnnounce()));

        Assert.Equal([nameof(ICharacterRepository.UpdateJobLevelExpAsync)], rec.Calls);
    }

    [Fact]
    public async Task CreateCharacter_StoresTheRaceInTheCreateItself_NoSecondWrite()
    {
        var (repo, rec) = NewRepo();

        var result = await CreateCharacterFlow.CreateAsync(1, "Kayate", 0, 0, "{}", 3, new Data(), repo);

        Assert.Equal(CreateCharacterFlow.CreateOutcome.Created, result.Outcome);
        Assert.Equal([nameof(ICharacterRepository.CreateCharacterAsync)], rec.Calls);
        Assert.Equal(3, rec.CreatedRace);
    }
}
