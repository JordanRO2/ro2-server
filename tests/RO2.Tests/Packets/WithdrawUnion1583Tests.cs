using RO2.Domain.Entities;
using System.Buffers.Binary;
using System.Text.Json;
using RO2.Network.Packets;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Wire layout of the union join and withdraw answers (RMI 2849 and 2850), as mapped by
/// <see cref="UnionRules.JoinResult"/> and <see cref="UnionRules.WithdrawResult"/>.
/// </summary>
public class WithdrawUnion1583Tests
{
 /// <summary>Join timestamp used by the 2849 fixture.</summary>
    private const long RetailJoinedAt = 0x6a6ef67b;

    [Fact]
    public void RetailJoin_Is2849ByteForByte()
    {
        uint unionId = BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString("65000000"));
        Assert.Equal(101u, unionId);
        Assert.Equal(UnionRules.ResultOk,
            UnionRules.JoinResult(null, unionId, unionExists: true, level: UnionRules.JoinMinLevel));

        using var writer = new PacketWriter();
        UnionRules.WriteAnsJoinUnion(writer, unionId, RetailJoinedAt, UnionRules.ResultOk);
        Assert.Equal(Convert.FromHexString("650000007bf66e6a000000000000"), writer.ToArray());
    }

    [Fact]
    public void JoinRefusals_FollowTheStoredMembershipAndTheLevelRule()
    {
        
        Assert.Equal(UnionRules.ResultUnknownUnion,
            UnionRules.JoinResult(new UnionMembership(101, RetailJoinedAt), 99, unionExists: false, level: 60));
        
        Assert.Equal(UnionRules.ResultAlreadyJoined,
            UnionRules.JoinResult(new UnionMembership(104, RetailJoinedAt), 101, unionExists: true, level: 60));
        
        Assert.Equal(UnionRules.ResultOk,
            UnionRules.JoinResult(new UnionMembership(101, RetailJoinedAt), 101, unionExists: true, level: 60));
        
        Assert.Equal(UnionRules.ResultLevelTooLow,
            UnionRules.JoinResult(null, 101, unionExists: true, level: UnionRules.JoinMinLevel - 1));
    }

    [Fact]
    public void RefusedJoin_CarriesTheRequestAndNoJoinTime()
    {
        using var writer = new PacketWriter();
        UnionRules.WriteAnsJoinUnion(writer, 101, UnionRules.NoJoinTime,
            UnionRules.ResultAlreadyJoined);
        Assert.Equal(Convert.FromHexString("65000000ffffffffffffffff" + "285e"), writer.ToArray());
    }

    [Fact]
    public void RetailWithdraw34sAfterJoining_IsRefusedWith24101()
    {
        
        uint unionId = BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString("65000000"));
        Assert.Equal(101u, unionId);

        ushort result = UnionRules.WithdrawResult(
            new UnionMembership(101, RetailJoinedAt), unionId, RetailJoinedAt + 34);
        Assert.Equal(UnionRules.ResultWithdrawTooSoon, result);

        
        using var writer = new PacketWriter();
        writer.WriteUInt16(result);
        Assert.Equal(Convert.FromHexString("255e"), writer.ToArray());
    }

    [Fact]
    public void AfterTwoWeeks_Succeeds()
    {
        long twoWeeks = 14L * 86400;
        Assert.Equal(UnionRules.ResultWithdrawTooSoon,
            UnionRules.WithdrawResult(new UnionMembership(101, RetailJoinedAt), 101, RetailJoinedAt + twoWeeks - 1));
        Assert.Equal(UnionRules.ResultOk,
            UnionRules.WithdrawResult(new UnionMembership(101, RetailJoinedAt), 101, RetailJoinedAt + twoWeeks));
    }

    [Fact]
    public void NoUnionShown_OrAnotherUnionStored_Is24105()
    {
        Assert.Equal(UnionRules.ResultNotInUnion,
            UnionRules.WithdrawResult(null, 0, RetailJoinedAt));
        Assert.Equal(UnionRules.ResultNotInUnion,
            UnionRules.WithdrawResult(new UnionMembership(101, RetailJoinedAt - 30L * 86400), 0, RetailJoinedAt));
        Assert.Equal(UnionRules.ResultNotInUnion,
            UnionRules.WithdrawResult(new UnionMembership(104, RetailJoinedAt - 30L * 86400), 101, RetailJoinedAt));
    }

    [Fact]
    public void UnionTheServerNeverStored_IsDropped()
    {
        
        
        Assert.Equal(UnionRules.ResultOk,
            UnionRules.WithdrawResult(null, 101, RetailJoinedAt));
        Assert.Equal(UnionRules.ResultOk,
            UnionRules.WithdrawResult(null, 0xCCCCCCCC, RetailJoinedAt));
    }

    [Fact]
    public async Task ResultCodes_AreTheStringsTheClientShows()
    {
        
        
        (ushort Code, string Id, string Text)[] expected =
        [
            (UnionRules.ResultUnknownUnion, "10678", "Unavailable Union ID"),
            (UnionRules.ResultWithdrawTooSoon, "10648", @"You can only leave the union\n2 weeks after joining it."),
            (UnionRules.ResultAlreadyJoined, "10680", "Already joined the Union"),
            (UnionRules.ResultNotInUnion, "10681", "Unregistered to a Union"),
            (UnionRules.ResultLevelTooLow, "10646",
                @"Joining unions in the Rune Midgarts Kingdom is\nonly available when level 50 and above."),
        ];
        Assert.Equal(new[] { 24100, 24101, 24104, 24105, 24106 }, expected.Select(e => (int)e.Code));

        await using var file = File.OpenRead(Path.Combine(DataDirectory(), "strings", "string_ui.json"));
        using var doc = await JsonDocument.ParseAsync(file);
        foreach (var (_, id, text) in expected)
            Assert.Equal(text, doc.RootElement.GetProperty(id).GetString());

        Assert.Equal(0, UnionRules.ResultOk);
        Assert.Equal(TimeSpan.FromDays(14), UnionRules.WithdrawLock);
        Assert.Equal(50, UnionRules.JoinMinLevel);
    }

    [Fact]
    public async Task EveryUnion_IsOfTheKingdomTheLevelRuleNames()
    {
        
        
        await using var file = File.OpenRead(Path.Combine(DataDirectory(), "Union_Info.json"));
        using var doc = await JsonDocument.ParseAsync(file);
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(1, r.GetProperty("Union_Group_ID").GetInt32()));
        Assert.All(rows, r => Assert.Equal(10639, r.GetProperty("String_Union_Group_ID").GetInt32()));
        Assert.Contains(rows, r => r.GetProperty("Union_ID").GetInt32() == 101);
    }

    private static string DataDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Union_Info.json")) && File.Exists(Path.Combine(candidate, "strings", "string_ui.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Union_Info.json and data/strings/string_ui.json");
    }
}
