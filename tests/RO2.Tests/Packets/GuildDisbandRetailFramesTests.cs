using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Guild disband: 1495 leads to 2688, then 1427 leads to 2472, 2488, 2472, 2488, 2562 and 2591. Expected
/// frames are reference frames; a second reference session adds two more disbands with the same burst.
/// The guild tests run on GuildService.Instance without Mongo (InitAsync is never called, so every
/// write-through is skipped); the guild a test creates is disbanded again.
/// </summary>
public class GuildDisbandRetailFramesTests
{
    private static int _nextChar = 1_980_000_000;
    private static uint NewCharId() => (uint)Interlocked.Increment(ref _nextChar);

    
    
    private const string Bar02e3869a =
        "0e010000f89cb1a7b3b6e00d0d000000020041a1090005000000000000000000000000010200dda0090005000000000000000000000000" +
        "02020009a209000500000000000000000000000003020031a309000100000000000000000000000004020061a40900050000000000000000" +
        "00000000050200559d09000100000000000000000000000006020025a5090001000000000000000000000000070200c1a409000100000000" +
        "000000000000000100020075a0090001000000000000000000000001010200f9a309000100000000000000000000000102020075a0090001" +
        "0000000000000000000000010503004a0000000000000000000000000000000202020095a30900010000000000000000000000";
    private const string Skills02e3869a =
        "01383800b3e3000000000b000000559d090075a00900dda0090041a1090009a2090031a3090095a30900f9a3090061a40900c1a4090025a50900";

    
    
    
    private const string Bar04396557 =
        "120200004cab56ab8f3c296f1a0000000200a17809000500000000000000000000000001020011770900050000000000000000000000" +
        "000202002d51090005000000000000000000000000030200f3510900030000000000000000000000000402006a5009000a000000000000" +
        "000000000000050200b59c5e000100000000000000000000000006020034bb1e0004000000000000000000000000070200d8c11e000400" +
        "000000000000000000000008020064cc1e0004000000000000000000000000090200f8ab1e00040000000000000000000000010002003" +
        "17a090005000000000000000000000001010200cd790900050000000000000000000000010202007a7709000a00000000000000000000" +
        "00010302006779090003000000000000000000000001040200427809000a000000000000000000000001050200599c5b000100000000000" +
        "000000000000106020013d11e00030000000000000000000000010702002ccd1e0004000000000000000000000001080200a5b01e000100" +
        "00000000000000000000010901005b384201f9000000ae3a5505a83d040e0204020003790900030000000000000000000000020502008" +
        "9d61e000100000000000000000000000206020039c21e00010000000000000000000000020702005ed51e0002000000000000000000000" +
        "002080200a8971e000400000000000000000000000209020089945b00010000000000000000000000";
    private const string Skills04396557 =
        "01545400b3e30000000012000000354f09006a5009002d510900f35109001d530900117709007a770900d777090042780900a1780900" +
        "0379090067790900cd790900317a0900937a0900950a3d008e0c3d0083e74c00";

    
    
    
    private const string Lines2723HeavensGate =
        "00004a000000120029d5010055d60100b9d601001dd7010099fc0100a90d0300bb3403001f35030083350300e735030047360300c95b0300" +
        "2d5c0300905c0300f95c03005e5d0300c25d0300265e0300";
    private const string SkillsInGuild04396557 =
        "017c7c00b3e3000000001c000000354f09006a5009002d510900f35109001d530900117709007a770900d777090042780900a1780900" +
        "0379090067790900cd790900317a0900937a090001093d0067093d00cd093d00950a3d00fb0a3d005f0b3d00c30b3d00270c3d008e0c3d00" +
        "f20c3d00560d3d00ba0d3d0083e74c00";

    private static byte[] Hex(string s) => Convert.FromHexString(s);
    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>Reads a 2472 frame back into the bar it describes: the uid and each 20-byte record.</summary>
    private static (ulong Uid, List<CharacterQuickSlot> Bar) ParseBar(byte[] body)
    {
        var uid = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(4));
        int n = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12));
        var bar = new List<CharacterQuickSlot>(n);
        for (var i = 0; i < n; i++)
        {
            var r = body.AsSpan(14 + 20 * i, 20);
            Assert.Equal(0, r[3]);
            bar.Add(new CharacterQuickSlot
            {
                Page = r[0], Slot = r[1], EntryType = r[2],
                EntryId = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
                Amount = checked((ushort)BinaryPrimitives.ReadUInt32LittleEndian(r[8..])),
                ItemUid = BinaryPrimitives.ReadUInt64LittleEndian(r[12..]),
            });
        }
        return (uid, bar);
    }

 /// <summary>Reads the (group, level) pairs of a 2488 frame: count at blob+8, ids group*100+level from blob+12.</summary>
    private static Dictionary<uint, byte> ParseSkills(byte[] body)
    {
        Assert.Equal(1, body[0]);                                   
        var blob = body.AsSpan(2);
        int n = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]));
        var skills = new Dictionary<uint, byte>();
        for (var i = 0; i < n; i++)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(blob[(12 + 4 * i)..]);
            skills[id / 100] = (byte)(id % 100);
        }
        return skills;
    }

    private static void AssertBurst(string barHex, string skillsHex)
    {
        var (uid, bar) = ParseBar(Hex(barHex));
        var skills = ParseSkills(Hex(skillsHex)).Select(kv => (kv.Key, kv.Value)).ToArray();

        var burst = GuildSkillBarRefresh.BuildBurst(uid, bar, skills);

        Assert.Equal(new[] { ZoneS2COpcodes.OnQuickSlotSaveData, ZoneS2COpcodes.OnUseSkillList,
                             ZoneS2COpcodes.OnQuickSlotSaveData, ZoneS2COpcodes.OnUseSkillList },
                     burst.Select(b => b.Opcode).ToArray());
        Assert.Equal(barHex, ToHex(burst[0].Body));
        Assert.Equal(skillsHex, ToHex(burst[1].Body));
        Assert.Equal(barHex, ToHex(burst[2].Body));
        Assert.Equal(skillsHex, ToHex(burst[3].Body));
    }

    [Fact]
    public void Burst_02e3869a_is_byte_exact() => AssertBurst(Bar02e3869a, Skills02e3869a);

    [Fact]
    public void Burst_04396557_is_byte_exact_with_an_item_cell() => AssertBurst(Bar04396557, Skills04396557);

    [Fact]
    public void An_empty_bar_still_sends_the_2472_and_an_unreadable_one_does_not()
    {
        var skills = ParseSkills(Hex(Skills02e3869a)).Select(kv => (kv.Key, kv.Value)).ToArray();

        
        
        const string retailEmpty = "0a000000af2eeaa7b3b6e00d0000";
        var (uid, bar) = ParseBar(Hex(retailEmpty));
        Assert.Empty(bar);
        var empty = GuildSkillBarRefresh.BuildBurst(uid, bar, skills);
        Assert.Equal(4, empty.Count);
        Assert.Equal(retailEmpty, ToHex(empty[0].Body));
        Assert.Equal(retailEmpty, ToHex(empty[2].Body));

        var unread = GuildSkillBarRefresh.BuildBurst(0x0de0b6b3a7b19cf8UL, null, skills);
        Assert.Equal(new[] { ZoneS2COpcodes.OnUseSkillList, ZoneS2COpcodes.OnUseSkillList },
                     unread.Select(b => b.Opcode).ToArray());
    }

    [Fact]
    public async Task Disband_takes_the_guilds_link_skills_out_of_the_2488()
    {
        var store = await LoadGuildSkillLevelAsync();
        var lines = Hex(Lines2723HeavensGate);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(lines.AsSpan(6));
        var guildSkills = Enumerable.Range(0, n)
            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(lines.AsSpan(8 + 4 * i))).ToList();
        Assert.Equal(18, guildSkills.Count);

        var charId = NewCharId();
        var player = new PlayerState { CharacterId = charId, LearnedSkills = ParseSkills(Hex(Skills04396557)) };
        var (guild, refusal) = await GuildService.Instance.CreateAsync(charId, "Disbander", 60, 6, $"Disband {Guid.NewGuid():N}");
        Assert.Equal(GuildService.CreateRefusal.None, refusal);
        var g = Assert.IsType<Guild>(guild);
        try
        {
            lock (g.Gate) g.Skills.AddRange(guildSkills);

            
            Assert.Equal(SkillsInGuild04396557,
                ToHex(WorldEntryService.BuildUseSkillList2488(GuildSkillBookLearn.SkillListFor(player, store))));

            Assert.Equal(GuildDelPossibleHandler.ResultAllowed,
                await GuildService.Instance.TryDisbandAsync(g, x => GuildDelPossibleHandler.DisbandRefusal(x, charId)));
            Assert.Null(GuildService.Instance.GetByCharId(charId));

            
            var burst = GuildSkillBarRefresh.BuildBurst(0x6f293c8fab56ab4cUL, Array.Empty<CharacterQuickSlot>(),
                GuildSkillBookLearn.SkillListFor(player, store));
            Assert.Equal(Skills04396557, ToHex(burst[1].Body));
            Assert.Equal(Skills04396557, ToHex(burst[3].Body));
        }
        finally { await GuildService.Instance.DisbandAsync(g); }
    }

    [Fact]
    public void DelPossible_codes_are_the_ones_the_2688_consumer_has_texts_for()
    {
        
        const uint master = 4242;
        Assert.Equal((ushort)7, GuildDelPossibleHandler.DisbandRefusal(null, master));

        var notMine = new Guild { MasterCharId = master + 1 };
        notMine.Members.Add(new GuildMember { CharId = master + 1 });
        notMine.Members.Add(new GuildMember { CharId = master });
        Assert.Equal((ushort)19, GuildDelPossibleHandler.DisbandRefusal(notMine, master));

        var withMembers = new Guild { MasterCharId = master };
        withMembers.Members.Add(new GuildMember { CharId = master });
        withMembers.Members.Add(new GuildMember { CharId = master + 1 });
        Assert.Equal((ushort)9, GuildDelPossibleHandler.DisbandRefusal(withMembers, master));

        var withItem = new Guild { MasterCharId = master };
        withItem.Members.Add(new GuildMember { CharId = master });
        withItem.Storage.Add(new GuildStorageItem { Uid = 1, ItemId = 1 });
        Assert.Equal((ushort)45, GuildDelPossibleHandler.DisbandRefusal(withItem, master));

        var withZeny = new Guild { MasterCharId = master, Gold = 1 };
        withZeny.Members.Add(new GuildMember { CharId = master });
        Assert.Equal((ushort)45, GuildDelPossibleHandler.DisbandRefusal(withZeny, master));

        var lone = new Guild { MasterCharId = master };
        lone.Members.Add(new GuildMember { CharId = master });
        Assert.Equal((ushort)0, GuildDelPossibleHandler.DisbandRefusal(lone, master));
    }

    [Fact]
    public void Delete_answers_the_vault_refusal_with_the_code_ShowResultCodeMessage_Guild_reads_as_7861()
    {
        
        Assert.Equal((ushort)41, GuildDeleteHandler.AnsGuildDeleteResult(GuildDelPossibleHandler.ResultStorageNotEmpty));
        Assert.Equal((ushort)7, GuildDeleteHandler.AnsGuildDeleteResult(GuildDelPossibleHandler.ResultNoGuild));
        Assert.Equal((ushort)9, GuildDeleteHandler.AnsGuildDeleteResult(GuildDelPossibleHandler.ResultHasMembers));
        Assert.Equal((ushort)19, GuildDeleteHandler.AnsGuildDeleteResult(GuildDelPossibleHandler.ResultNotMaster));
        Assert.Equal((ushort)0, GuildDeleteHandler.AnsGuildDeleteResult(GuildDelPossibleHandler.ResultAllowed));
    }

 /// <summary>A store holding only data/Guild_Skill_Level.json (every loader tolerates a missing file).</summary>
    private static async Task<GameDataStore> LoadGuildSkillLevelAsync()
    {
        var source = Path.Combine(DataDir(), "Guild_Skill_Level.json");
        var dir = Path.Combine(Path.GetTempPath(), "ro2-guildskill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(source, Path.Combine(dir, "Guild_Skill_Level.json"));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            Assert.NotEmpty(store.GuildSkillLevel);
            return store;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

 /// <summary>The repo's data/ folder, found by walking up from the test binaries (any machine, and CI).</summary>
    private static string DataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Guild_Skill_Level.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Guild_Skill_Level.json");
    }
}
