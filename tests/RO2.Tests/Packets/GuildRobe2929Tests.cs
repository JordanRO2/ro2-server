using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 2929 AnsGuildListRegisteredRobe. A guild master whose guild has never bought a robe receives the 58-byte Basic Robe answer; every other member receives the 33-byte code 0x13 body.
/// </summary>
public class GuildRobe2929Tests
{
 /// <summary>Master's 2929 answer for a guild without a purchased robe.</summary>
    private const string RetailMasterFreshGuild =
        "360000000000" +
        "24f6c100abaaaaaa2af8d840000000000000000081af9fff80" +
        "0100" +
        "24f6c100abaaaaaa2af8d840000000000000000081af9fff80";

 /// <summary>Non-master 2929 answer body (code 0x13).</summary>
    private const string RetailNonMaster = "1d000000130000000000000000000000000000000000000041fa00000000800000";

 /// <summary>Offsets of the two robe records' f64 date fields inside the 58-byte master body.</summary>
    private static readonly int[] DateOffsets = { 6 + 4, 33 + 4 };

    private static byte[] FromHex(string h) => Convert.FromHexString(h);

 /// <summary>Master body with its dates re-expressed in the local time zone. The server derives this field rather than copying it (see GuildRobeRecord.BasicRobeDate).</summary>
    private static byte[] RetailWithLocalDates(string hex, IEnumerable<int> dateOffsets)
    {
        var b = FromHex(hex);
        foreach (var off in dateOffsets)
            BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(off, 8), GuildRobeRecord.BasicRobeDate);
        return b;
    }

    [Fact]
    public void Master_answer_is_retails_fresh_guild_form()
    {
        var body = GuildPackets.BuildAnsGuildListRegisteredRobeBasic();

        Assert.Equal(58, body.Length);
        Assert.Equal(RetailWithLocalDates(RetailMasterFreshGuild, DateOffsets), body);
    }

    [Fact]
    public void Master_answer_lists_the_current_robe_first()
    {
        var body = GuildPackets.BuildAnsGuildListRegisteredRobeBasic();

        Assert.Equal((uint)body.Length - 4, BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));              
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(31)));             
        Assert.Equal(body.AsSpan(6, 25).ToArray(), body.AsSpan(33, 25).ToArray());             
        Assert.Equal(GuildRobeRecord.BasicRobeItemId, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(33)));
    }

 /// <summary>The robe window's current robe is the record the guild panel (2559) carries, so the two
 /// can never name different robes.</summary>
    [Fact]
    public void Master_answer_carries_the_guild_lines_robe_record()
    {
        var body = GuildPackets.BuildAnsGuildListRegisteredRobeBasic();
        var record = GuildRobeRecord.InfoForm();

        Assert.Equal(record, body.AsSpan(6, 25).ToArray());
        Assert.Equal(record, body.AsSpan(33, 25).ToArray());
    }

 /// <summary>With the original date the record matches the reference bytes exactly, whatever the local time zone.</summary>
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
