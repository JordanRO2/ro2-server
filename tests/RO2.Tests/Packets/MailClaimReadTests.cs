using RO2.Domain.Entities;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 1396 ReqMailGetItem and 2447 OnMailGetItem, and RMI 1397 ReqMailRead and 2446 OnMailRead, verified against reference frames.
/// </summary>
/// <remarks>
/// The claim pair uses two system mails of one owner, each carrying three of a stackable item (Stack_Max 99). The first claim lands
/// under the mail's own item uid, in container 2 slot 15. The second claim joins that stack, and its own uid is removed.
/// Reading the first mail again shows the item with its uid zeroed.
/// The date fields of 2446 are OLE dates in local time, so a test cannot reproduce them exactly: the date slots (body 19..50)
/// are taken from the test frame and every other byte is compared.
/// </remarks>
public class MailClaimReadTests
{
    private const uint Owner = 0x0B6F7711;
    private const ulong MailA = 0x0E4B4B8B08641902;
    private const ulong MailB = 0x0E4B4B8B0863BAEA;
    private const ulong ItemUidA = 0x0E043DA81298EA7C;
    private const ulong ItemUidB = 0x0E043DA81299184F;
    private const uint Template = 22401997;     
    private const short C2Slot15 = 20 + 15;     

    private static readonly string Read2446A =
        "c100000011776f0b0000021964088b4b4b0e01f7603fd9c69de64000000000c69de640f7603fd9069ae64000000000069ae640"
        + "7cea9812a83d040ecdd355010000036600" + Zeros(109)
        + "0000000000000000" + "050031003100380032003500";

    private static readonly string Claim2447A =
        "029100910058e311776f0b0000021964088b4b4b0e"
        + "7cea9812a83d040ecdd35501000003020f" + Zeros(109) + "01";

    private static readonly string Claim2447B =
        "029100910058e311776f0b0000eaba63088b4b4b0e"
        + "7cea9812a83d040ecdd35501000006020f" + Zeros(109) + "01";

    private static readonly string Reread2446A =
        "c100000011776f0b0000021964088b4b4b0e01f7603fd9c69de64000000000c69de640f7603fd9069ae64000000000069ae640"
        + "0000000000000000cdd355010000036600" + Zeros(109)
        + "0000000000000000" + "050031003100380032003500";

    
    private static readonly string Auction2446 =
        "b700000011bb6307000024df38088b4b4b0e015be28c4a0794e640000000000794e6405be28c4a4790e640000000004790e640"
        + "c01d9a0da83d040eda1f2c040000016700" + Zeros(109)
        + "6500000000000000" + "0000";

    
    private static readonly string Failed2446 =
        "b70000001168440a4b3f000000000000000000abaaaaaa2af8d84000000000c5f77f00abaaaaaa2af8d8400000000000000000"
        + Zeros(126) + "0000000000000000" + "0000";

    private static string Zeros(int bytes) => new string('0', bytes * 2);

    private static byte[] Hex(string h) => Convert.FromHexString(h);

    private static MailMessage SystemMail(ulong mailUid, ulong itemUid) => new()
    {
        Uid = mailUid,
        Kind = 22,
        IsRead = true,
        AttachedItemUid = itemUid,
        AttachedItemId = Template,
        AttachedItemCount = 3,
        AttachedItemDurability = 0,
        Body = "11825",
    };

 /// <summary>Asserts that the frame equals the reference hex outside the date slots (body 19..50).</summary>
    private static void AssertEqualOutsideDates(string retailHex, byte[] ours)
    {
        var expected = Hex(retailHex);
        Assert.Equal(expected.Length, ours.Length);
        Array.Copy(ours, 19, expected, 19, 32);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(ours));
    }

    [Fact]
    public void Claim2447_KeepsTheMailItemUid_InAFreeCell()
    {
        var row = MailService.AttachmentAsBagRow(SystemMail(MailA, ItemUidA), 1, ItemUidA, C2Slot15);
        var rec = WorldEntryService.BuildItemRecord126(row);
        var body = MailPackets.BuildMailGetItem2447(Owner, 0, MailA, rec, MailPackets.GetItemKeepMail);
        Assert.Equal(Claim2447A.ToUpperInvariant(), Convert.ToHexString(body));
    }

    [Fact]
    public void Claim2447_JoinsTheStackItFits_KeepingItsUidAndCell()
    {
        var mailB = SystemMail(MailB, ItemUidB);
        var stack = MailService.AttachmentAsBagRow(SystemMail(MailA, ItemUidA), 1, ItemUidA, C2Slot15);
        var bag = new List<InventoryItem> { stack };

        var join = MailService.FindJoinStack(bag, mailB, 99);
        Assert.Same(stack, join);

        
        join!.Count += mailB.AttachedItemCount;
        var body = MailPackets.BuildMailGetItem2447(Owner, 0, MailB, WorldEntryService.BuildItemRecord126(join),
            MailPackets.GetItemKeepMail);
        Assert.Equal(Claim2447B.ToUpperInvariant(), Convert.ToHexString(body));
    }

    [Fact]
    public void FindJoinStack_NeedsRoom_TheSameState_AndAStackableTemplate()
    {
        
        var mail = new MailMessage { AttachedItemId = 70000883, AttachedItemCount = 84 };
        var fiftyTwo = new InventoryItem { Id = 1, ItemId = 70000883, Count = 52, Slot = 13, StorageType = 0 };
        Assert.Null(MailService.FindJoinStack(new[] { fiftyTwo }, mail, 99));
        Assert.Same(fiftyTwo, MailService.FindJoinStack(new[] { fiftyTwo }, mail, 255));

        
        Assert.Null(MailService.FindJoinStack(new[] { fiftyTwo }, mail, 1));

        
        var refined = new InventoryItem { Id = 2, ItemId = 70000883, Count = 1, Slot = 14, StorageType = 0, Refine = 3 };
        var stored = new InventoryItem { Id = 3, ItemId = 70000883, Count = 1, Slot = 0, StorageType = 2 };
        Assert.Null(MailService.FindJoinStack(new[] { refined, stored }, mail, 255));

        
        var low = new InventoryItem { Id = 4, ItemId = 70000883, Count = 1, Slot = 2, StorageType = 0 };
        Assert.Same(low, MailService.FindJoinStack(new[] { fiftyTwo, low }, mail, 255));
    }

    [Fact]
    public void Read2446_ShowsTheAttachmentInTheMailContainer()
    {
        var mail = SystemMail(MailA, ItemUidA);
        var body = MailPackets.BuildMailRead2446(Owner, MailA, true, DateTime.UtcNow, 0,
            MailPackets.BuildAttachmentRecord126(mail, claimedThisEntry: false), mail.Body);
        AssertEqualOutsideDates(Read2446A, body);
    }

    [Fact]
    public void Read2446_AfterTheClaim_KeepsTheItemWithoutItsUid_UntilTheNextMapLoad()
    {
        var mail = SystemMail(MailA, ItemUidA);
        mail.ItemClaimed = true;

        var body = MailPackets.BuildMailRead2446(Owner, MailA, true, DateTime.UtcNow, 0,
            MailPackets.BuildAttachmentRecord126(mail, claimedThisEntry: true), mail.Body);
        AssertEqualOutsideDates(Reread2446A, body);

        
        Assert.Equal(new byte[MailPackets.ItemRecordLength], MailPackets.BuildAttachmentRecord126(mail, claimedThisEntry: false));
    }

    [Fact]
    public void Read2446_AuctionItemMail_UsesContainer103()
    {
        var mail = new MailMessage
        {
            Uid = 0x0E4B4B8B0838DF24,
            Kind = AuctionMailKind.Cancelled,
            IsRead = true,
            AttachedItemUid = 0x0E043DA80D9A1DC0,
            AttachedItemId = 70000602,
            AttachedItemCount = 1,
            AttachedItemDurability = 0,
            AuctionPrice = 101,
        };
        var body = MailPackets.BuildMailRead2446(0x0763BB11, mail.Uid, true, DateTime.UtcNow, mail.AuctionPrice,
            MailPackets.BuildAttachmentRecord126(mail, claimedThisEntry: false), string.Empty);
        AssertEqualOutsideDates(Auction2446, body);
    }

    [Fact]
    public void Read2446_UnresolvedUid_IsRetailsErrorRecord()
    {
        var body = MailPackets.BuildMailReadFailed2446(0x0A446811, MailPackets.ReadInvalidMail);

        
        
        var expected = Hex(Failed2446);
        Assert.Equal(25568.666666666668, BitConverter.ToDouble(expected, 19));
        var epoch = BitConverter.GetBytes(DateTime.UnixEpoch.ToLocalTime().ToOADate());
        epoch.CopyTo(expected, 19);
        epoch.CopyTo(expected, 35);
        Array.Clear(expected, 31, 4);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(body));
    }

 /// <summary>
 /// A claim is forgotten by any map load (1304) after it, as MailService.ClaimedThisEntry requires. Each ForgetClaimsOnMapLoad call below
 /// stands for one 1304, which AnsMapLoadingHandler issues on every map load. The same-map reload and the A to B to A round trip end with
 /// the MapId the claim saw, so they pass only if the rule does not depend on the map id.
 /// </summary>
    [Fact]
    public void ClaimedThisEntry_LastsUntilTheNextMapLoad()
    {
        var player = new PlayerState { CharacterId = 1, MapId = 1100 };
        Assert.False(MailService.ClaimedThisEntry(player, MailA));

        MailService.NoteClaimedThisEntry(player, MailA);
        Assert.True(MailService.ClaimedThisEntry(player, MailA));
        Assert.False(MailService.ClaimedThisEntry(player, MailB));

        
        Assert.False(MailService.ClaimedThisEntry(new PlayerState { CharacterId = 1, MapId = 1100 }, MailA));

        
        
        MailService.ForgetClaimsOnMapLoad(player);
        Assert.Equal(1100u, player.MapId);
        Assert.False(MailService.ClaimedThisEntry(player, MailA));

        
        MailService.NoteClaimedThisEntry(player, MailB);
        Assert.True(MailService.ClaimedThisEntry(player, MailB));

        
        player.MapId = 1101;
        MailService.ForgetClaimsOnMapLoad(player);
        player.MapId = 1100;
        MailService.ForgetClaimsOnMapLoad(player);
        Assert.False(MailService.ClaimedThisEntry(player, MailB));
        Assert.False(MailService.ClaimedThisEntry(player, MailA));

        
        MailService.ForgetClaimsOnMapLoad(new PlayerState { CharacterId = 2, MapId = 1100 });
    }
}
