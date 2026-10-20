using System.Buffers.Binary;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Guild name uniqueness: guild creation (1426) and rename (1586) share one name gate, so no two requests can claim the same name.
/// </summary>
/// <remarks>
/// Tests run on GuildService.Instance without MongoDB. Every guild a test creates is disbanded again.
/// </remarks>
public class GuildNameGateTests
{
    private static int _nextChar = 1_900_000_000;
    private static uint NewCharId() => (uint)Interlocked.Increment(ref _nextChar);
    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";
    private static int _nextName;
 /// <summary>A unique name that passes GuildRenameRules.CheckFormat and the 1585 name check: at most 14 characters, and no character three times in a row (counter digits are separated by 'x').</summary>
    private static string ShortUnique(string prefix)
        => prefix + string.Concat(Interlocked.Increment(ref _nextName).ToString().Select(d => $"{d}x"));

    private static async Task<Guild> NewGuildAsync(string name)
    {
        var (g, refusal) = await GuildService.Instance.CreateAsync(NewCharId(), "Founder", 10, 1, name);
        Assert.Equal(GuildService.CreateRefusal.None, refusal);
        return Assert.IsType<Guild>(g);
    }

    private static async Task DisbandAllAsync(IEnumerable<Guild?> guilds)
    {
        foreach (var g in guilds)
            if (g != null) await GuildService.Instance.DisbandAsync(g);
    }

    private static Task NoPayment() => Task.CompletedTask;

    [Fact]
    public async Task Rename_refuses_a_name_another_guild_carries_in_any_case()
    {
        var a = await NewGuildAsync(Unique("Alpha"));
        var b = await NewGuildAsync(Unique("Beta"));
        try
        {
            var before = a.Name;
            var paid = false;
            var (refusal, old) = await GuildService.Instance.RenameAsync(a, b.Name.ToUpperInvariant(),
                () => { paid = true; return Task.CompletedTask; });

            Assert.Equal(GuildService.RenameRefusal.NameTaken, refusal);
            Assert.Equal(before, old);
            Assert.Equal(before, a.Name);
            Assert.False(paid);   
        }
        finally { await DisbandAllAsync([a, b]); }
    }

 /// <summary>Own-guild rule: a guild cannot be renamed to its current name in different letter case; the server refuses it with 2854 code 5.</summary>
    [Fact]
    public async Task Rename_refuses_the_guilds_own_name_in_another_case()
    {
        
        
        var a = await NewGuildAsync("HEVN's FANS");
        try
        {
            var (refusal, _) = await GuildService.Instance.RenameAsync(a, "Hevn's Fans", NoPayment);
            Assert.Equal(GuildService.RenameRefusal.NameTaken, refusal);
            Assert.Equal("HEVN's FANS", a.Name);
            Assert.Equal(GuildRenameRules.NameTaken, GuildRenameRules.Check("Hevn's Fans"));
        }
        finally { await DisbandAllAsync([a]); }
    }

    [Fact]
    public async Task While_a_rename_is_paying_both_names_are_taken()
    {
        var oldName = ShortUnique("B");
        var newName = ShortUnique("A");
        var a = await NewGuildAsync(oldName);
        var made = new List<Guild?>();
        try
        {
            var inside = new List<(GuildService.CreateRefusal New, GuildService.CreateRefusal Old, ushort Check)>();
            var (refusal, old) = await GuildService.Instance.RenameAsync(a, newName, async () =>
            {
                
                var (g1, r1) = await GuildService.Instance.CreateAsync(NewCharId(), "Racer", 10, 1, newName);
                var (g2, r2) = await GuildService.Instance.CreateAsync(NewCharId(), "Racer", 10, 1, oldName);
                made.Add(g1);
                made.Add(g2);
                inside.Add((r1, r2, GuildRenameRules.Check(newName)));
            });

            Assert.Equal(GuildService.RenameRefusal.None, refusal);
            Assert.Equal(oldName, old);
            Assert.Equal(newName, a.Name);
            var seen = Assert.Single(inside);
            Assert.Equal(GuildService.CreateRefusal.NameTaken, seen.New);
            Assert.Equal(GuildService.CreateRefusal.NameTaken, seen.Old);
            Assert.Equal(GuildRenameRules.NameTaken, seen.Check);
            Assert.All(made, g => Assert.Null(g));

            
            Assert.False(GuildService.Instance.IsNameTaken(oldName));
            Assert.True(GuildService.Instance.IsNameTaken(newName));
            var (g3, r3) = await GuildService.Instance.CreateAsync(NewCharId(), "Later", 10, 1, oldName);
            made.Add(g3);
            Assert.Equal(GuildService.CreateRefusal.None, r3);
        }
        finally { await DisbandAllAsync([a, .. made]); }
    }

    [Fact]
    public async Task A_payment_that_throws_gives_the_old_name_back()
    {
        var oldName = Unique("Kept");
        var newName = Unique("Lost");
        var a = await NewGuildAsync(oldName);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => GuildService.Instance.RenameAsync(a, newName,
                () => throw new InvalidOperationException("inventory write failed")));

            Assert.Equal(oldName, a.Name);
            Assert.True(GuildService.Instance.IsNameTaken(oldName));
            Assert.False(GuildService.Instance.IsNameTaken(newName));

            
            var (refusal, _) = await GuildService.Instance.RenameAsync(a, newName, NoPayment);
            Assert.Equal(GuildService.RenameRefusal.None, refusal);
            Assert.Equal(newName, a.Name);
        }
        finally { await DisbandAllAsync([a]); }
    }

    [Fact]
    public async Task A_disbanded_guild_is_not_renamed_and_pays_nothing()
    {
        var a = await NewGuildAsync(Unique("Gone"));
        await GuildService.Instance.DisbandAsync(a);
        var paid = false;
        var (refusal, _) = await GuildService.Instance.RenameAsync(a, Unique("Ghost"),
            () => { paid = true; return Task.CompletedTask; });
        Assert.Equal(GuildService.RenameRefusal.NotRegistered, refusal);
        Assert.False(paid);
    }

 /// <summary>Race between a 1426 and a 1586 for one free name, started together on the thread pool many times. Exactly one may succeed each time.</summary>
    [Fact]
    public async Task A_creation_and_a_rename_racing_for_one_name_never_both_win()
    {
        var a = await NewGuildAsync(Unique("Racer"));
        var made = new List<Guild?>();
        try
        {
            for (int i = 0; i < 300; i++)
            {
                var name = Unique("Contested");
                var founder = NewCharId();
                var create = Task.Run(() => GuildService.Instance.CreateAsync(founder, "Founder", 10, 1, name));
                var rename = Task.Run(() => GuildService.Instance.RenameAsync(a, name, async () => await Task.Yield()));
                await Task.WhenAll(create, rename);
                var (created, createRefusal) = await create;
                var (renameRefusal, _) = await rename;
                made.Add(created);

                var createWon = createRefusal == GuildService.CreateRefusal.None;
                var renameWon = renameRefusal == GuildService.RenameRefusal.None;
                Assert.True(createWon ^ renameWon, $"round {i}: create {createRefusal}, rename {renameRefusal}");
                if (createWon) Assert.NotEqual(name, a.Name);
                else Assert.Equal(name, a.Name);
            }
        }
        finally { await DisbandAllAsync([a, .. made]); }
    }

 /// <summary>
 /// Wire around the gate: the 2855 rename answer (body 00 00) for a successful 1586, and the refusal body 2c 00, which is sent twice per refused 1586.
 /// </summary>
    [Fact]
    public void Retail_1586_body_parses_and_2855_answers_are_retail_bytes()
    {
        var body = Convert.FromHexString(
            "24000000369c8c12a83d040e0d00480065006100760065006e002700730020004700610074006500");
        Assert.True(GuildRenameRules.TryParse(body, out var uid, out var name));
        Assert.Equal(0x0e043da8128c9c36UL, uid);
        Assert.Equal("Heaven's Gate", name);
        Assert.Equal((uint)(body.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(GuildRenameRules.Ok, GuildRenameRules.CheckFormat(name));

        Assert.Equal("0000", Convert.ToHexString(Answer(GuildRenameRules.Ok)).ToLowerInvariant());
        Assert.Equal("2c00", Convert.ToHexString(Answer(GuildRenameRules.NameNotUsable)).ToLowerInvariant());
    }

 /// <summary>2855's body as GuildRenameHandler writes it: one u16.</summary>
    private static byte[] Answer(ushort code)
    {
        using var w = new PacketWriter(2);
        w.WriteUInt16(code);
        return w.ToArray();
    }
}
