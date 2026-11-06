using System.Buffers.Binary;
using RO2.Guilds.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 2929 AnsGuildListRegisteredRobe. The guild master receives the guild's current robe and the list of robes it may
/// wear (the Basic Robe and every robe the guild has unlocked); every other member receives the 33-byte code 0x13 body.
/// </summary>
public class GuildRobe2929Tests
{
    /// <summary>Non-master 2929 answer body (code 0x13).</summary>
    private const string RetailNonMaster = "1d000000130000000000000000000000000000000000000041fa00000000800000";

    private const int CodeOffset = 4;
    private const int CurrentRobeOffset = 6;
    private const int RecordLen = 25;
    private const int CountOffset = CurrentRobeOffset + RecordLen;          // 31
    private const int ListOffset = CountOffset + 2;                         // 33

    private static byte[] FromHex(string h) => Convert.FromHexString(h);
    private static uint ItemIdAt(byte[] body, int off) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(off));
    private static uint ColorAt(byte[] body, int off) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(off + 20));

    [Fact]
    public void Fresh_guild_lists_only_the_basic_robe_it_wears()
    {
        var g = new Guild();
        var ids = GuildRobeRecord.SelectableRobeItemIds(g);
        Assert.Equal(new[] { GuildRobeRecord.BasicRobeItemId }, ids);

        var body = GuildPackets.BuildAnsGuildListRegisteredRobe(g, ids);

        Assert.Equal((uint)body.Length - 4, BinaryPrimitives.ReadUInt32LittleEndian(body));         // inner length
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(CodeOffset)));          // master code 0
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(CountOffset)));
        Assert.Equal(GuildRobeRecord.InfoForm(), body.AsSpan(CurrentRobeOffset, RecordLen).ToArray());  // worn = Basic
        Assert.Equal(GuildRobeRecord.BasicRobeItemId, ItemIdAt(body, ListOffset));
    }

    [Fact]
    public void Master_answer_lists_the_current_robe_then_basic_and_every_owned_robe()
    {
        var g = new Guild { RobeItemId = 12711464, RobeColorCode = 0xFF88A868 };
        g.OwnedRobes["12711461"] = 0xFF112233;
        g.OwnedRobes["12711464"] = 0xFF88A868;
        var ids = GuildRobeRecord.SelectableRobeItemIds(g);
        Assert.Equal(new uint[] { GuildRobeRecord.BasicRobeItemId, 12711461, 12711464 }, ids);

        var body = GuildPackets.BuildAnsGuildListRegisteredRobe(g, ids);

        Assert.Equal(ids.Count, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(CountOffset)));
        for (int i = 0; i < ids.Count; i++)
            Assert.Equal(ids[i], ItemIdAt(body, ListOffset + i * RecordLen));
        // Worn robe is the current record, with the guild colour.
        Assert.Equal(12711464u, ItemIdAt(body, CurrentRobeOffset));
        Assert.Equal(0xFF88A868u, ColorAt(body, CurrentRobeOffset));
        // Each list entry carries its own colour: Basic the default, each owned robe its stored colour.
        Assert.Equal(GuildRobeRecord.BasicRobeColor, ColorAt(body, ListOffset + 0 * RecordLen));     // Basic
        Assert.Equal(0xFF112233u, ColorAt(body, ListOffset + 1 * RecordLen));                        // 12711461
        Assert.Equal(0xFF88A868u, ColorAt(body, ListOffset + 2 * RecordLen));                        // 12711464 (worn)
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
