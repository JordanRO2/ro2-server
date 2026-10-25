using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Services;

/// <summary>
/// Revocation rules of <see cref="ChannelMoveTicketStore"/>: at most one ticket is kept per account. A newer
/// ticket revokes the account's older ones, a successful 1421 claim revokes any other ticket the account still
/// holds, and <see cref="ChannelMoveTicketStore.RevokeAccount"/> drops them on demand. Single use and the 30
/// second lifetime are unchanged.
/// </summary>
public sealed class ChannelMoveTicketRevocationTests
{
    private const string First2556 =
        "24a1190000000a4b78b583240e408f07579266930a553100320038002e003200340031002e00390033002e0032003100310000000000331c0e1000000000c8420000c8420000c842004cab56ab8f3c296f";
    private const string First1421 =
        "0a4b78b583240e408f07579266930a5524a119003100390032002e003100360038002e0030002e00380000004691030103000000";
    private const string Second2556 =
        "24a1190000003668644ffacec44b9eaa02f3725a97693100320038002e003200340031002e00390033002e0032003100310000000000211c5704000000008b4400405e440000aa42004cab56ab8f3c296f";
    private const string Second1421 =
        "3668644ffacec44b9eaa02f3725a976924a119003100390032002e003100360038002e0030002e00380000004691030103000000";

 /// <summary>Character UID as carried in the 2556 frame at offset 73.</summary>
    private const ulong CharacterUid = 0x6F293C8FAB56AB4C;

 /// <summary>
 /// Channel handle the store carries through to 2557. The 2556 frames above answer no 1420 (their map field is
 /// set), so they carry no handle.
 /// </summary>
    private const uint ChannelHandle = 0x06614081;

    private static readonly DateTimeOffset T0 = new(2026, 7, 21, 5, 50, 10, 922, TimeSpan.Zero);

    private static (byte[] Token, uint Account) From2556(string hex)
    {
        var body = Convert.FromHexString(hex);
        Assert.Equal(81, body.Length);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4))); 
        Assert.Equal(CharacterUid, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(73)));
        return (body[6..22], BinaryPrimitives.ReadUInt32LittleEndian(body));
    }

    private static (byte[] Token, uint Account) From1421(string hex)
    {
        var body = Convert.FromHexString(hex);
        Assert.Equal(52, body.Length);
        return (body[..16], BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

 /// <summary>The claim sequence: each 1421 claims the ticket its 2556 carried, once.</summary>
    [Fact]
    public void RetailSequence_EachClaimFindsItsTicket_Once()
    {
        var store = new ChannelMoveTicketStore();
        var (t1, account) = From2556(First2556);
        var (c1, claimed1) = From1421(First1421);
        var (t2, _) = From2556(Second2556);
        var (c2, claimed2) = From1421(Second1421);
        Assert.Equal(1679652u, account);
        Assert.Equal(account, claimed1);
        Assert.Equal(account, claimed2);

        store.Issue(t1, account, CharacterUid, ChannelHandle, T0);
        var ticket = store.Consume(c1, claimed1, T0 + TimeSpan.FromMilliseconds(109));
        Assert.NotNull(ticket);
        Assert.Equal(account, ticket.AccountId);
        Assert.Equal(ChannelHandle, ticket.ChannelHandle);
        Assert.Null(store.Consume(c1, claimed1, T0 + TimeSpan.FromMilliseconds(110))); 

        var second = T0 + TimeSpan.FromSeconds(176.873);
        store.Issue(t2, account, CharacterUid, ChannelHandle, second);
        Assert.NotNull(store.Consume(c2, claimed2, second + TimeSpan.FromMilliseconds(160)));
        Assert.Equal(0, store.PendingCount);
    }

    [Fact]
    public void NewTicket_RevokesTheAccountsEarlierOne()
    {
        var store = new ChannelMoveTicketStore();
        var (t1, account) = From2556(First2556);
        var (t2, _) = From2556(Second2556);

        store.Issue(t1, account, CharacterUid, ChannelHandle, T0);
        store.Issue(t2, account, CharacterUid, ChannelHandle, T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(1, store.PendingCount);
        Assert.Null(store.Consume(t1, account, T0 + TimeSpan.FromSeconds(2)));
        Assert.NotNull(store.Consume(t2, account, T0 + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void NewTicket_LeavesOtherAccountsAlone()
    {
        var store = new ChannelMoveTicketStore();
        var (t1, account) = From2556(First2556);
        var (t2, _) = From2556(Second2556);
        const uint other = 1679653;

        store.Issue(t1, other, CharacterUid, ChannelHandle, T0);
        store.Issue(t2, account, CharacterUid, ChannelHandle, T0);

        Assert.Equal(2, store.PendingCount);
        Assert.NotNull(store.Consume(t1, other, T0));
        Assert.NotNull(store.Consume(t2, account, T0));
    }

 /// <summary>
 /// A claim with the right token but the wrong account neither takes the ticket nor revokes anything, so it
 /// cannot strand the client's real move.
 /// </summary>
    [Fact]
    public void WrongAccountClaim_DestroysNothing()
    {
        var store = new ChannelMoveTicketStore();
        var (t1, account) = From2556(First2556);

        store.Issue(t1, account, CharacterUid, ChannelHandle, T0);
        Assert.Null(store.Consume(t1, account + 1, T0));
        Assert.Equal(1, store.PendingCount);
        Assert.NotNull(store.Consume(t1, account, T0));
    }

    [Fact]
    public void RevokeAccount_DropsOnlyThatAccountsTicket()
    {
        var store = new ChannelMoveTicketStore();
        var (t1, account) = From2556(First2556);
        var (t2, _) = From2556(Second2556);
        const uint other = 7;

        store.Issue(t1, account, CharacterUid, ChannelHandle, T0);
        store.Issue(t2, other, CharacterUid, ChannelHandle, T0);

        Assert.Equal(1, store.RevokeAccount(account));
        Assert.Equal(0, store.RevokeAccount(account));
        Assert.Null(store.Consume(t1, account, T0));
        Assert.NotNull(store.Consume(t2, other, T0));
    }

 /// <summary>The lifetime is 30 seconds: a ticket is valid up to 30 seconds after its 2556 and refused after that.</summary>
    [Fact]
    public void Expiry_IsUnchanged()
    {
        var store = new ChannelMoveTicketStore();
        var (t1, account) = From2556(First2556);
        var (t2, _) = From2556(Second2556);

        store.Issue(t1, account, CharacterUid, ChannelHandle, T0);
        Assert.Null(store.Consume(t1, account, T0 + ChannelMoveTicketStore.Lifetime + TimeSpan.FromMilliseconds(1)));

        store.Issue(t2, account, CharacterUid, ChannelHandle, T0);
        Assert.NotNull(store.Consume(t2, account, T0 + ChannelMoveTicketStore.Lifetime));
    }

 /// <summary>Issuing still sweeps other accounts' expired tickets.</summary>
    [Fact]
    public void Issue_SweepsExpiredTicketsOfOtherAccounts()
    {
        var store = new ChannelMoveTicketStore();
        var (t1, account) = From2556(First2556);
        var (t2, _) = From2556(Second2556);

        store.Issue(t1, 99, CharacterUid, ChannelHandle, T0);
        store.Issue(t2, account, CharacterUid, ChannelHandle, T0 + TimeSpan.FromMinutes(1));

        Assert.Equal(1, store.PendingCount);
    }

 /// <summary>
 /// Two 1420 requests from one account racing each other leave exactly one ticket, and it is one of theirs.
 /// Revoking and adding happen as one step.
 /// </summary>
    [Fact]
    public async Task ConcurrentIssues_ForOneAccount_LeaveExactlyOneTicket()
    {
        var store = new ChannelMoveTicketStore();
        const uint account = 1679652;
        var tokens = Enumerable.Range(0, 64)
            .Select(i => Enumerable.Range(0, ChannelMoveTicketStore.TokenBytes).Select(b => (byte)(i * 17 + b)).ToArray())
            .ToArray();

        await Task.WhenAll(tokens.Select(t => Task.Run(() => store.Issue(t, account, CharacterUid, ChannelHandle))));

        Assert.Equal(1, store.PendingCount);
        Assert.Single(tokens, t => store.Consume(t, account) is not null);
    }

    [Fact]
    public void Issue_StillRejectsATokenOfTheWrongWidth()
    {
        var store = new ChannelMoveTicketStore();
        Assert.Throws<ArgumentException>(() => store.Issue(new byte[15], 1, CharacterUid, ChannelHandle));
        Assert.Null(store.Consume(new byte[17], 1));
    }
}
