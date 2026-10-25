using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// The combat class (create[19], Character.Job) and the creation profession (create[21]) kept apart: the stored-row
/// heal at login, the char list [65]/[73], and the 2683 profession block.
/// </summary>
public class CharacterClassProfessionTests
{
 /// <summary>A row as CharacterCreateHandler stores it: the 23-byte create block kept verbatim, create[0] in
 /// Gender, and <paramref name="job"/> in Character.Job.</summary>
    private static Character Created(string createHex, byte job, byte level = 1)
    {
        var create = Convert.FromHexString(createHex);
        var appearance = CharacterSerializer.ParseCreateAppearance(new PacketReader(create));
        return new Character
        {
            Id = 3,
            Name = "T",
            Level = level,
            Job = job,
            Gender = create[0],
            MapId = 1100,
            AppearanceJson = CharacterSerializer.ToJson(appearance),
        };
    }

    private static byte[] CharListHeader(Character c)
    {
        using var w = new PacketWriter();
        CharacterSerializer.WriteCharacterList(w, 1, new[] { c });
        
        return w.ToArray().AsSpan(10, 102).ToArray();
    }

    
    private const string Jordan1Create = "02010703ff00050000000603ff00010000000010000400";

    
    
    
    private const string ZtRandom3Create = "0202060dff0006000000060eff00010000000010000200";
    private const string ZtRandom3RetailEntry50To76 = "0202060dff0006000000060eff000110014c040000000002010600";

    [Fact]
    public void Heal_TheOldHandlersAlterRow_BecomesAnAlter_Once()
    {
        var c = Created(Jordan1Create, job: 4);

        Assert.True(WorldEntryService.TryHealStoredCombatClass(c));
        Assert.Equal(16, c.Job);
        
        Assert.False(WorldEntryService.TryHealStoredCombatClass(c));
        Assert.Equal(16, c.Job);
    }

    [Fact]
    public void Heal_DoesNotMoveWhatTheClientIsTold()
    {
        var c = Created(Jordan1Create, job: 4);
        byte classBefore = WorldEntryService.CombatJobByte(c), professionBefore = WorldEntryService.CreationProfessionByte(c);
        var listBefore = CharListHeader(c);

        WorldEntryService.TryHealStoredCombatClass(c);

        Assert.Equal(classBefore, WorldEntryService.CombatJobByte(c));
        Assert.Equal(professionBefore, WorldEntryService.CreationProfessionByte(c));
        Assert.Equal(listBefore, CharListHeader(c));
        Assert.Equal(16, classBefore);
        Assert.Equal(4, professionBefore);
    }

    [Theory]
    [InlineData(ZtRandom3Create, 17)]  
    [InlineData(ZtRandom3Create, 16)]  
    [InlineData("0101010aff0001000000060fff00010000000004000400", 4)] 
    public void Heal_LeavesJobChangedCurrentAndSameByteRowsAlone(string createHex, int job)
    {
        var c = Created(createHex, (byte)job);
        Assert.False(WorldEntryService.TryHealStoredCombatClass(c));
        Assert.Equal(job, c.Job);
    }

    [Fact]
    public void Heal_LeavesARowWithoutACreateBlockAlone()
    {
        var c = new Character { Name = "T", Job = 4 };
        Assert.False(WorldEntryService.TryHealStoredCombatClass(c));
        Assert.Equal(4, c.Job);
    }

    [Fact]
    public async Task HealedRow_DropsTheProfessionClassesSkills_KeepsItsOwnAndTheProfessionSkill()
    {
        
        
        
        var store = await LoadShippedTalentTableAsync();
        var alterStarters = StatService.StarterSkillGroups(store, 16);
        Assert.Equal(new uint[] { 6101, 6104, 6111 }, alterStarters);

        var learned = new uint[] { 4101, 4103, 4108, 50300 }.Concat(alterStarters).ToArray();
        Assert.Equal(new uint[] { 4101, 4103, 4108 }, LoginPCHandler.TalentGroupsOutsideClass(store, 16, learned));
        
        Assert.Equal(new uint[] { 6101, 6104, 6111 }, LoginPCHandler.TalentGroupsOutsideClass(store, 4, learned));
    }

    [Fact]
    public void CharList_ZtRandom3_Entry50To76_IsRetails()
    {
        
        
        foreach (var job in new byte[] { 16, 2 })
        {
            var header = CharListHeader(Created(ZtRandom3Create, job));
            Assert.Equal(ZtRandom3RetailEntry50To76, Convert.ToHexString(header.AsSpan(50, 27)).ToLowerInvariant());
        }
    }

    [Fact]
    public void CharList_65FollowsAJobChange_73DoesNot()
    {
        
        
        var c = Created(ZtRandom3Create, job: 16);
        var before = CharListHeader(c);
        c.Job = 17;
        var after = CharListHeader(c);

        Assert.Equal(16, before[65]);
        Assert.Equal(17, after[65]);
        Assert.Equal(2, before[73]);
        Assert.Equal(2, after[73]);
    }

    [Fact]
    public void ChangeProJobInfo2683_CarriesTheProfessionBlock()
    {
        
        
        var c = Created(ZtRandom3Create, job: 16);
        c.JobLevel = 50;
        c.JobExp = 0x0102030405060708UL;

        var body = QuestProgressPackets.BuildChangeProJobInfo2683(
            WorldEntryService.CreationProfessionByte(c), c.JobLevel, c.JobExp);

        Assert.Equal("0113130039e40000000002320807060504030201" + "00", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void NfyPCollectionSkill2920_IsThePetsBookRecord()
    {
        
        
        var body = CollectionPackets.BuildNfyPCollectionSkill(new PetCollectionState(83000001, 210011, 0, 0));
        Assert.Equal("c17af2045b340300000000", Convert.ToHexString(body).ToLowerInvariant());
    }

    private static async Task<GameDataStore> LoadShippedTalentTableAsync()
    {
        
        var source = Path.Combine(RequireDataDirectory(), "Talent_Table.json");
        var dir = Path.Combine(Path.GetTempPath(), "ro2-class-" + Guid.NewGuid().ToString("N"));
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
