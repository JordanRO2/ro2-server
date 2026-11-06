using System.Buffers.Binary;
using RO2.Guilds.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 2929 AnsGuildListRegisteredRobe. The guild master receives the guild's current robe and the list of robes it may
/// pick (the Basic Robe and the Guild_Robe table); every other member receives the 33-byte code 0x13 body.
/// </summary>
public class GuildRobe2929Tests
{
    /// <summary>Non-master 2929 answer body (code 0x13).</summary>
    private const string RetailNonMaster = "1d000000130000000000000000000000000000000000000041fa00000000800000";

    /// <summary>The robes the master may pick: the Basic Robe and the five Guild_Robe table robes.</summary>
    private static readonly uint[] Selectable =
        { GuildRobeRecord.BasicRobeItemId, 12711461, 12711462, 12711463, 12711464, 12711465 };

    private const int CodeOffset = 4;
    private const int CurrentRobeOffset = 6;
    private const int RecordLen = 25;
    private const int CountOffset = CurrentRobeOffset + RecordLen;          // 31
    private const int ListOffset = CountOffset + 2;                         // 33

    private static byte[] FromHex(string h) => Convert.FromHexString(h);
    private static uint ItemIdAt(byte[] body, int off) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(off));
    private static uint ColorAt(byte[] body, int off) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(off + 20));

    [Fact]
    public void Master_answer_lists_the_current_robe_then_every_pickable_robe()
    {
        var body = GuildPackets.BuildAnsGuildListRegisteredRobe(new Guild(), Selectable);

        Assert.Equal((uint)body.Length - 4, BinaryPrimitives.ReadUInt32LittleEndian(body));         // inner length
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(CodeOffset)));          // master code 0
        Assert.Equal(Selectable.Length, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(CountOffset)));
        for (int i = 0; i < Selectable.Length; i++)
            Assert.Equal(Selectable[i], ItemIdAt(body, ListOffset + i * RecordLen));
    }

    [Fact]
    public void Fresh_guild_wears_the_basic_robe()
    {
        var body = GuildPackets.BuildAnsGuildListRegisteredRobe(new Guild(), Selectable);

        Assert.Equal(GuildRobeRecord.InfoForm(), body.AsSpan(CurrentRobeOffset, RecordLen).ToArray());
        Assert.Equal(GuildRobeRecord.BasicRobeItemId, ItemIdAt(body, CurrentRobeOffset));
        Assert.Equal(GuildRobeRecord.BasicRobeColor, ColorAt(body, CurrentRobeOffset));
    }

    [Fact]
    public void Chosen_robe_is_the_current_robe_and_carries_the_guilds_colour()
    {
        var g = new Guild { RobeItemId = 12711464, RobeColorCode = 0xFF88A868 };

        var body = GuildPackets.BuildAnsGuildListRegisteredRobe(g, Selectable);

        // Current robe = the chosen robe with the guild colour.
        Assert.Equal(12711464u, ItemIdAt(body, CurrentRobeOffset));
        Assert.Equal(0xFF88A868u, ColorAt(body, CurrentRobeOffset));
        // Its list entry carries the colour; the others carry none.
        int worn = Array.IndexOf(Selectable, 12711464u);
        Assert.Equal(0xFF88A868u, ColorAt(body, ListOffset + worn * RecordLen));
        Assert.Equal(0u, ColorAt(body, ListOffset)); // the Basic entry (not worn)
    }

    [Fact]
    public void Basic_robe_record_with_retails_date_is_retails_bytes()
    {
        const string retailRecord = "24f6c100abaaaaaa2af8d840000000000000000081af9fff80";
        var retailDate = BinaryPrimitives.ReadDoubleLittleEndian(FromHex("abaaaaaa2af8d840"));
        var record = new byte[25];

        GuildRobeRecord.WriteInfoForm(record, retailDate);

        Assert.Equal(FromHex(retailRecord), record);
    }

    [Fact]
    public void Retail_basic_robe_date_is_the_unix_epoch_in_utc_minus_8()
    {
        var retail = BinaryPrimitives.ReadDoubleLittleEndian(FromHex("abaaaaaa2af8d840"));
        var epochInUtcMinus8 = DateTime.UnixEpoch.AddHours(-8).ToOADate();

        Assert.Equal(BitConverter.DoubleToInt64Bits(epochInUtcMinus8), BitConverter.DoubleToInt64Bits(retail));
        Assert.Equal(DateTime.UnixEpoch.ToLocalTime().ToOADate(), GuildRobeRecord.BasicRobeDate);
    }

    [Fact]
    public void Non_master_answer_is_retails_code_0x13_form()
    {
        Assert.Equal(FromHex(RetailNonMaster), GuildPackets.BuildAnsGuildListRegisteredRobeEmpty());
    }
}
