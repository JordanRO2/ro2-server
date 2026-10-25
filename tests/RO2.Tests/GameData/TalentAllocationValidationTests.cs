using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.GameData;

/// <summary>
/// P2: <see cref="StatService.IsTalentAllocationLegal"/>, the server-side gate the 1310
/// ReqPCUseTalentPoint handler runs before persisting a talent allocation. Pinned to the shipped
/// data/Talent_Table.json so the class membership, level cap and unlock-level gates match the real tree.
/// Job 6 (Swordsman) rows used here: 1102 (max 5, unlock 1), 1101 (max 3, unlock 1), 1204 (max 5, unlock
/// 25); 6301 is a job-18 row only. The point budget is intentionally not a gate (see the method remarks).
/// </summary>
public class TalentAllocationValidationTests
{
    [Fact]
    public async Task LegalAllocationOfClassSkillsPasses()
    {
        var store = await LoadShippedTalentTableAsync();

        Assert.True(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(1102u, (byte)5), (1101u, (byte)3)], Held((1110, 1)), out var reason), reason);
        Assert.Equal("", reason);
    }

    [Fact]
    public async Task ZeroGroupOrLevelEntriesAreIgnored()
    {
        var store = await LoadShippedTalentTableAsync();

        // The handler persists neither, so they cannot make a request illegal on their own.
        Assert.True(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(0u, (byte)5), (1102u, (byte)0)], held: null, out _));
    }

    [Fact]
    public async Task CrossClassSkillIsRefused()
    {
        var store = await LoadShippedTalentTableAsync();

        // 6301 is a job-18 talent; a job-6 character may not learn it.
        Assert.False(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(6301u, (byte)1)], held: null, out var reason));
        Assert.Contains("not a talent skill", reason);
    }

    [Fact]
    public async Task JunkSkillIdIsRefused()
    {
        var store = await LoadShippedTalentTableAsync();

        Assert.False(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(123456u, (byte)1)], held: null, out var reason));
        Assert.Contains("not a talent skill", reason);
    }

    [Fact]
    public async Task LevelAboveTheSkillCapIsRefused()
    {
        var store = await LoadShippedTalentTableAsync();

        // 1101's max is 3.
        Assert.False(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(1101u, (byte)4)], held: null, out var reason));
        Assert.Contains("exceeds its max", reason);
    }

    [Fact]
    public async Task SkillTakenBelowItsUnlockLevelIsRefused()
    {
        var store = await LoadShippedTalentTableAsync();

        // 1204 unlocks at character level 25.
        Assert.False(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 10, [(1204u, (byte)1)], held: null, out var reason));
        Assert.Contains("unlocks at character level 25", reason);
    }

    [Fact]
    public async Task SkillAtItsUnlockLevelPasses()
    {
        var store = await LoadShippedTalentTableAsync();

        Assert.True(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 25, [(1204u, (byte)1)], Held((1201, 1)), out var reason), reason);
    }

    [Fact]
    public async Task OutOfRangeJobIsRefused()
    {
        var store = await LoadShippedTalentTableAsync();

        Assert.False(StatService.IsTalentAllocationLegal(store, job: 0, level: 50, [(1102u, (byte)1)], held: null, out _));
        Assert.False(StatService.IsTalentAllocationLegal(store, job: 99, level: 50, [(1102u, (byte)1)], held: null, out _));
    }

    // ---- CP-G4: prerequisites against the post-change tree; CP-G5: no level below the held one ----

    private static Dictionary<uint, byte> Held(params (uint Group, byte Level)[] tree)
        => tree.ToDictionary(t => t.Group, t => t.Level);

    [Fact]
    public async Task PrerequisiteChainInOneBatchIsLegal()
    {
        var store = await LoadShippedTalentTableAsync();

        // 1117 needs 1102 and 1108 needs 1117 (Talent_Table, job 6): all three at once read the post-change tree.
        Assert.True(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(1102u, (byte)1), (1117u, (byte)1), (1108u, (byte)1)], Held(), out var reason), reason);
    }

    [Fact]
    public async Task ChainOrderInTheVectorDoesNotMatter()
    {
        var store = await LoadShippedTalentTableAsync();

        Assert.True(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(1108u, (byte)1), (1117u, (byte)1), (1102u, (byte)1)], held: null, out var reason), reason);
    }

    [Fact]
    public async Task CrossJobPrerequisiteThatThisJobCannotLearnIsNotEnforced()
    {
        var store = await LoadShippedTalentTableAsync();

        // 6102 (Require_Job 98304 = jobs 16 & 17) names 6204@1 as a prerequisite, but 6204 is a job-17-only skill
        // (Require_Job 65536). A job-16 player can learn 6102 yet can NEVER hold 6204, so the cross-job prereq must
        // be skipped for job 16 (else the skill is permanently unlearnable)...
        Assert.True(StatService.IsTalentAllocationLegal(
            store, job: 16, level: 50, [(6102u, (byte)1)], held: null, out var r16), r16);
        // ...but job 17 CAN learn 6204, so there the prerequisite still applies and 6102 alone is refused.
        Assert.False(StatService.IsTalentAllocationLegal(
            store, job: 17, level: 50, [(6102u, (byte)1)], held: null, out _));
    }

    [Fact]
    public async Task ChildAloneWithoutItsParentIsRefused()
    {
        var store = await LoadShippedTalentTableAsync();

        Assert.False(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(1108u, (byte)1)], Held(), out var reason));
        Assert.Contains("prerequisite", reason);
        Assert.Contains("1108", reason);
    }

    [Fact]
    public async Task ChildIsLegalWhenItsParentIsAlreadyHeld()
    {
        var store = await LoadShippedTalentTableAsync();

        Assert.True(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(1108u, (byte)1)], Held((1117, 1)), out var reason), reason);
    }

    [Fact]
    public async Task ParentRequestedAtLevelZeroDoesNotCountAsHeld()
    {
        var store = await LoadShippedTalentTableAsync();

        // A zero-level entry is ignored by the handler, so it cannot stand in for the parent.
        Assert.False(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(1117u, (byte)0), (1108u, (byte)1)], Held(), out _));
    }

    [Fact]
    public async Task LoweringAHeldLevelIsRefused()
    {
        var store = await LoadShippedTalentTableAsync();

        Assert.False(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(1110u, (byte)1)], Held((1110, 3)), out var reason));
        Assert.Contains("below the held level 3", reason);
    }

    [Fact]
    public async Task KeepingOrRaisingAHeldLevelIsLegal()
    {
        var store = await LoadShippedTalentTableAsync();

        Assert.True(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(1110u, (byte)3)], Held((1110, 3)), out var same), same);
        Assert.True(StatService.IsTalentAllocationLegal(
            store, job: 6, level: 50, [(1110u, (byte)4)], Held((1110, 3)), out var raised), raised);
    }

    private static async Task<GameDataStore> LoadShippedTalentTableAsync()
    {
        var source = Path.Combine(RequireDataDirectory(), "Talent_Table.json");
        var dir = Path.Combine(Path.GetTempPath(), "ro2-talent-val-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(source, Path.Combine(dir, "Talent_Table.json"));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Talent_Table.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "no ancestor of " + AppContext.BaseDirectory + " holds data/Talent_Table.json");
    }
}
