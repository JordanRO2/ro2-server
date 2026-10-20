using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 2897 OnSkillChannelingCompleteACK (<see cref="SkillChannelingPackets"/>) and the channel state behind its
/// result (<see cref="PlayerState.TryEndChannel"/>). The expected bodies are 6-byte unencrypted payloads.
/// </summary>
public class SkillChannelingCompleteAck2897Tests
{
    [Theory]
    [InlineData("8a9e09000000", 630410u, (ushort)0)] 
    [InlineData("859e09000000", 630405u, (ushort)0)]
    [InlineData("8a9e09000100", 630410u, (ushort)1)] 
    [InlineData("859e09000100", 630405u, (ushort)1)]
    public void Build_ReproducesRetailFrames(string hex, uint skillId, ushort result)
    {
        Assert.Equal(Convert.FromHexString(hex), SkillChannelingPackets.BuildSkillChannelingCompleteAck2897(skillId, result));
    }

    [Fact]
    public void Channel_CompletesOnceForTheSkillInProgress()
    {
        var player = new PlayerState();
        player.BeginChannel(630410);
        Assert.Equal(630410u, player.ActiveChannelSkillId);

        Assert.False(player.TryEndChannel(630405)); 
        Assert.Equal(630410u, player.ActiveChannelSkillId);

        Assert.True(player.TryEndChannel(630410));
        Assert.Equal(0u, player.ActiveChannelSkillId);
        Assert.False(player.TryEndChannel(630410)); 
    }

    [Fact]
    public void Channel_NoneInProgress_NeverCompletes()
    {
        var player = new PlayerState();
        Assert.False(player.TryEndChannel(630410));
        Assert.False(player.TryEndChannel(0));
    }

    [Fact]
    public void ChannelledSkills_AreTheSkillDurationRows()
    {
        var set = SkillChannelingPackets.LoadChannelledSkills(Path.Combine(RequireDataDirectory(), "Skill_Table_SkillLevel.json"));
        Assert.NotNull(set);
        
        Assert.Contains(630410u, set);
        Assert.Contains(630405u, set);
        Assert.Equal(11, set.Count);
        Assert.All(set, id => Assert.True(id / 100 == 6304 || id == 7372101, $"unexpected channelled skill {id}"));
        Assert.DoesNotContain(410801u, set); 
    }

    [Fact]
    public void ChannelledSkills_MissingTable_IsNull()
    {
        Assert.Null(SkillChannelingPackets.LoadChannelledSkills(Path.Combine(Path.GetTempPath(), "no-such-dir", "Skill_Table_SkillLevel.json")));
    }

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Skill_Table_SkillLevel.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "no ancestor of " + AppContext.BaseDirectory + " holds data/Skill_Table_SkillLevel.json");
    }
}
