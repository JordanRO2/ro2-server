using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Layout tests for 2825, 2701, 2833, 1392, 1626 and 2924. No reference frame exists for these opcodes, so each
/// test pins the layout the client's reader or builder defines. Not verified against the reference server.
/// </summary>
public class PartyAndEggBlankA7Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>2825: the dispatch reads no payload; the stub takes no payload argument.</summary>
    [Fact]
    public void NfyDungeonFindPartyCancel2825_IsEmpty()
    {
        Assert.Empty(PartyMatchingIntoDungeonHandler.BuildNfyDungeonFindPartyCancel2825());
    }

 /// <summary>
 /// 2701: a 20-byte vector read. Bytes 4 to 19 must stay zero. Result 0 shows nothing in the consumer.
 /// </summary>
    [Fact]
    public void AnsJurisdictionIndun2701_Is20BytesWithZeroVector()
    {
        var body = PartyJurisdictionIndunHandler.BuildAnsJurisdictionIndun2701(0);
        Assert.Equal(20, body.Length);
        Assert.Equal(new string('0', 40), Hex(body));
        var withCode = PartyJurisdictionIndunHandler.BuildAnsJurisdictionIndun2701(0x1234);
        Assert.Equal("3412" + new string('0', 36), Hex(withCode));
    }

 /// <summary>2833: one u16, which the consumer only logs.</summary>
    [Fact]
    public void AnsReJoinDungeon2833_IsU16Zero()
    {
        Assert.Equal("0000", Hex(PartyMatchingReJoinDungeonHandler.BuildAnsReJoinDungeon2833()));
    }

 /// <summary>
 /// 1392 from the client builder alone: a compact byte array whose blob is a u16 length, a u16 tag, a u32 session
 /// handle and a u32 argument.
 /// </summary>
    [Fact]
    public void ReqPartySummary1392_ParsesTheEnvelopeAndArg()
    {
        var body = Convert.FromHexString("010c" + "0c00" + "2fe3" + "11223306" + "78563412");
        Assert.True(PartySummaryHandler.TryParseRequest(body, out var arg));
        Assert.Equal(0x12345678u, arg);

        
        Assert.False(PartySummaryHandler.TryParseRequest(Convert.FromHexString("010c0c0030e31122330678563412"), out _));
        Assert.False(PartySummaryHandler.TryParseRequest(Convert.FromHexString("010c0d002fe31122330678563412"), out _));
        Assert.False(PartySummaryHandler.TryParseRequest(Convert.FromHexString("010d0c002fe31122330678563412"), out _));
        Assert.False(PartySummaryHandler.TryParseRequest(Convert.FromHexString("01080800" + "2fe3" + "11223306"), out _));
    }

 /// <summary>
 /// 1626: byte 0 is the item's bag, byte 1 its slot, byte 2 the spinner times, and bytes 3 to 6 the pet NPC id
 /// (unaligned). The builder sends 7 bytes.
 /// </summary>
    [Fact]
    public void ReqCombinEggBlank1626_ParsesBagSlotTimesPet()
    {
        
        var body = Convert.FromHexString("021b03c17af204");
        Assert.True(PCollectionCombinEggBlankHandler.TryParseRequest(body, out var bag, out var slot, out var times, out var pet));
        Assert.Equal(2, bag);
        Assert.Equal(27, slot);
        Assert.Equal(3, times);
        Assert.Equal(83000001u, pet);
        Assert.False(PCollectionCombinEggBlankHandler.TryParseRequest(body.AsSpan(0, 6), out _, out _, out _, out _));
    }

 /// <summary>2924: a u32 id and a u16 result, no envelope (6 bytes). The consumer refreshes the combination window on result 0.</summary>
    [Fact]
    public void AnsCombinEggBlank2924_IsIdThenResult()
    {
        Assert.Equal("c17af2040000", Hex(CollectionPackets.BuildCollectionIdResultAck(83000001, 0)));
    }

 /// <summary>The pet recipe scaled by times and grouped by item: one pet has four distinct pieces, another has four pieces of the same item.</summary>
    [Fact]
    public void PieceNeeds_ScalesByTimesAndGroupsRepeatedPieces()
    {
        var poring = new PetInfo
        {
            NPC_ID = 83000001,
            ItemID_Piece1 = 22400011, Num_Piece1 = 1,
            ItemID_Piece2 = 22400012, Num_Piece2 = 1,
            ItemID_Piece3 = 22400013, Num_Piece3 = 1,
            ItemID_Piece4 = 22400014, Num_Piece4 = 1,
        };
        Assert.Equal(
            [(22400011u, 2), (22400012u, 2), (22400013u, 2), (22400014u, 2)],
            PCollectionCombinEggBlankHandler.PieceNeeds(poring, 2));

        var blocked = new PetInfo
        {
            NPC_ID = 83000005,
            ItemID_Piece1 = 22402114, Num_Piece1 = 1,
            ItemID_Piece2 = 22402114, Num_Piece2 = 1,
            ItemID_Piece3 = 22402114, Num_Piece3 = 1,
            ItemID_Piece4 = 22402114, Num_Piece4 = 1,
        };
        Assert.Equal([(22402114u, 12)], PCollectionCombinEggBlankHandler.PieceNeeds(blocked, 3));
    }
}
