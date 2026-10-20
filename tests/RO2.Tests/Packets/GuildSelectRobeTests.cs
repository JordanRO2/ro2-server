using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1633 ReqGuildSelectRobe and its answers 2928 AnsGuildSelectRobe and 2927 NfyGuildRobeChanged, handled by GuildSelectRobeHandler and <see cref="GuildRobeChangedPackets"/>.
/// </summary>
public class GuildSelectRobeTests
{
 /// <summary>Zone-entry 2927 push for a guild whose Basic Robe is recoloured: the guild-line record in the 2927 wrapper, not a selection answer.</summary>
    private const string RetailBasicRobe2927 = "1a00000024f6c100abaaaaaa2af8d84000000000000000008e72eaff8000";

 /// <summary>Selection records of the other three 2927 pushes, each sent with a 2928 answer of code 0.</summary>
    [Theory]
    [InlineData("1a00000028f6c100a71835fc4193e640000000004193e64068a888ff8000")]   
    [InlineData("1a00000000000000889349ea2efde140000000002efde14081af9fff8000")]   
    [InlineData("1a00000028f6c1005813cf0a4293e640000000004293e64050cdb0ff8000")]   
    public void Robe_changed_2927_wraps_a_record_as_retail_does(string retailHex)
    {
        var retail = Convert.FromHexString(retailHex);

        Assert.Equal(retail, GuildRobeChangedPackets.BuildNfyGuildRobeChanged2927(retail.AsSpan(4, 25)));
    }

    [Fact]
    public void Robe_changed_2927_with_the_guild_line_record_is_the_dumps_push_frame()
    {
        
        var record = new byte[25];
        GuildRobeRecord.WriteInfoForm(record, BinaryPrimitives.ReadDoubleLittleEndian(Convert.FromHexString("abaaaaaa2af8d840")));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), 0xFFEA728E);

        Assert.Equal(Convert.FromHexString(RetailBasicRobe2927), GuildRobeChangedPackets.BuildNfyGuildRobeChanged2927(record));
    }

    [Fact]
    public void Robe_changed_2927_sent_on_a_basic_robe_pick_is_the_dumps_selection_answer()
    {
        
        
        
        var body = GuildRobeChangedPackets.BuildNfyGuildRobeChanged2927(GuildRobeChangedPackets.BasicRobeSelectedRecord());

        Assert.Equal(Convert.FromHexString("1a00000000000000889349ea2efde140000000002efde14081af9fff8000"), body);
        Assert.NotEqual(GuildRobeRecord.InfoForm(), body.AsSpan(4, 25).ToArray());   
    }

    [Fact]
    public void Select_answer_2928_is_the_dumps_frame()
    {
        Assert.Equal(Convert.FromHexString("0000"),
            GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectAccepted));
    }

 /// <summary>Not verified against a reference body: code 0x13 is one of the codes (6, 7, 19) for which the client's 2928 consumer returns without changing the robe window.</summary>
    [Fact]
    public void Not_master_answer_2928_is_a_code_the_consumer_drops()
    {
        var body = GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectNotMaster);

        Assert.Equal(Convert.FromHexString("1300"), body);
        Assert.Contains(BinaryPrimitives.ReadUInt16LittleEndian(body), new ushort[] { 6, 7, 19 });
    }
}
