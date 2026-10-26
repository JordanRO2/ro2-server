using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Economy;

/// <summary>
/// Economy hardening, cluster M (mail + cash mall): the guard-first mail claims (E-H7), the trade/bind gate on mailed
/// items (E-H5-mail), the NPC gate on the 1394 mailbox open (E-H8-mail-open) and the cash-mall caps and throttles
/// (E-H12). Runs on <see cref="EconomyFixture"/>: invariant A (zeny conservation) / D (no negative gold) on the
/// zeny claim, B (items) and C (unique placement) on the item claim.
/// </summary>
public class MailCashMallClusterMTests
{
    private const uint Receiver = 41;
    private const uint ItemTemplate = 7001;

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static MailService Svc(EconomyFixture fx)
    {
        var pm = new PlayerStateManager(new NoDirtySink());
        return new MailService(fx.MailRepo, fx.CharacterRepo, pm,
            new BroadcastService(pm, NullLogger<BroadcastService>.Instance), NullLogger.Instance);
    }

    // ---------------------------------------------------------------- E-H7: item claim

    [Fact]
    public async Task TwoConcurrentItemClaims_ExactlyOneWins_UnderEveryInterleaving()
    {
        EconomySnapshot before = null!;
        var summary = await RepoScheduler.ExploreAllAsync(
            create: () =>
            {
                var fx = new EconomyFixture();
                fx.AddPlayer(Receiver);
                fx.GiveMail(Receiver, attachedItemId: ItemTemplate, attachedItemUid: 9000, attachedItemCount: 2);
                before = fx.Snapshot();
                return fx;
            },
            ops: fx =>
            {
                var svc = Svc(fx);
                var uid = fx.Mail.Mails.Single().Uid;
                return new List<Func<Task>>
                {
                    () => svc.ClaimItemAsync(Receiver, uid, fx.InventoryRepo, null, 5),
                    () => svc.ClaimItemAsync(Receiver, uid, fx.InventoryRepo, null, 6),
                };
            },
            verify: (fx, result) =>
            {
                result.ThrowIfFaulted();
                var rows = fx.Inventory.Rows;
                var row = Assert.Single(rows);                       // exactly one grant
                Assert.Equal((ItemTemplate, 2), (row.ItemId, row.Count));
                Assert.True(fx.Mail.Mails.Single().ItemClaimed);
                var after = fx.Snapshot();
                EconomyInvariants.AssertItemsConserved(before, after);
                EconomyInvariants.AssertUniquePlacement(after);
                return Task.CompletedTask;
            });
        Assert.True(summary.Runs >= 1);
    }

    [Fact]
    public async Task ClaimItem_SecondClaimGetsNothing()
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(Receiver);
        var mail = fx.GiveMail(Receiver, attachedItemId: ItemTemplate, attachedItemUid: 9000);
        var svc = Svc(fx);

        var first = await svc.ClaimItemAsync(Receiver, mail.Uid, fx.InventoryRepo, null, 3);
        var second = await svc.ClaimItemAsync(Receiver, mail.Uid, fx.InventoryRepo, null, 4);

        Assert.NotNull(first.Placed);
        Assert.Null(second.Placed);
        Assert.Single(fx.Inventory.Rows);
    }

    [Fact]
    public async Task ClaimItem_FaultAfterNewRowPlacement_LeavesMailClaimable_NoItemMinted()
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(Receiver);
        var mail = fx.GiveMail(Receiver, attachedItemId: ItemTemplate, attachedItemUid: 9000, attachedItemCount: 2);
        var svc = Svc(fx);
        var faulty = FaultAfter.Wrap(fx.InventoryRepo, nameof(IInventoryRepository.AddItemAsync));

        var before = fx.Snapshot();
        await Assert.ThrowsAsync<IOException>(() => svc.ClaimItemAsync(Receiver, mail.Uid, faulty, null, 3));

        Assert.Empty(fx.Inventory.Rows);                          // the row the write put in is taken back out
        Assert.False(fx.Mail.Mails.Single().ItemClaimed);         // still claimable
        EconomyFixture.CheckAll(before, fx.Snapshot());

        // ... and a healthy retry claims it, once.
        var retry = await svc.ClaimItemAsync(Receiver, mail.Uid, fx.InventoryRepo, null, 3);
        Assert.NotNull(retry.Placed);
        var row = Assert.Single(fx.Inventory.Rows);
        Assert.Equal((ItemTemplate, 2), (row.ItemId, row.Count));
        EconomyFixture.CheckAll(before, fx.Snapshot());
    }

    [Fact]
    public async Task ClaimItem_FaultAfterStackJoin_RestoresTheStackAndTheMail()
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(Receiver);
        var stack = fx.GiveItem(Receiver, ItemTemplate, count: 3, slot: 0);
        var mail = fx.GiveMail(Receiver, attachedItemId: ItemTemplate, attachedItemUid: 9000, attachedItemCount: 2);
        var svc = Svc(fx);
        var faulty = FaultAfter.Wrap(fx.InventoryRepo, nameof(IInventoryRepository.AdjustCountAsync));

        var before = fx.Snapshot();
        await Assert.ThrowsAsync<IOException>(() => svc.ClaimItemAsync(Receiver, mail.Uid, faulty, stack, -1));

        Assert.Equal(3, Assert.Single(fx.Inventory.Rows).Count);  // 3 + 2 - 2
        Assert.False(fx.Mail.Mails.Single().ItemClaimed);
        EconomyFixture.CheckAll(before, fx.Snapshot());
    }

    [Fact]
    public async Task ClaimItem_NoRoom_NeverTouchesTheFlag()
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(Receiver);
        var mail = fx.GiveMail(Receiver, attachedItemId: ItemTemplate, attachedItemUid: 9000);

        var claim = await Svc(fx).ClaimItemAsync(Receiver, mail.Uid, fx.InventoryRepo, null, -1);

        Assert.Null(claim.Placed);
        Assert.False(fx.Mail.Mails.Single().ItemClaimed);
    }

    // ---------------------------------------------------------------- E-H7: zeny claim

    [Fact]
    public async Task TwoConcurrentZenyClaims_ExactlyOneWins_UnderEveryInterleaving()
    {
        var summary = await RepoScheduler.ExploreAllAsync(
            create: () =>
            {
                var fx = new EconomyFixture();
                fx.AddPlayer(Receiver, gold: 100);
                fx.GiveMail(Receiver, gold: 5000);
                return fx;
            },
            ops: fx =>
            {
                var svc = Svc(fx);
                var uid = fx.Mail.Mails.Single().Uid;
                return new List<Func<Task>>
                {
                    async () => Grant(fx, await svc.ClaimGoldAsync(Receiver, uid)),
                    async () => Grant(fx, await svc.ClaimGoldAsync(Receiver, uid)),
                };
            },
            verify: (fx, result) =>
            {
                result.ThrowIfFaulted();
                Assert.Equal(5100, fx.Players[Receiver].Gold);        // 100 + 5000, once
                Assert.True(fx.Mail.Mails.Single().GoldClaimed);
                EconomyInvariants.AssertNoNegativeGold(fx.Snapshot());
                return Task.CompletedTask;
            });
        Assert.True(summary.Runs >= 1);

        static void Grant(EconomyFixture fx, (MailMessage? mail, long granted) r)
        {
            if (r.granted > 0) fx.Players[Receiver].Credit(r.granted);
        }
    }

    [Fact]
    public async Task ZenyClaim_ConservesZenyAcrossMailAndWallet()
    {
        var fx = new EconomyFixture();
        var player = fx.AddPlayer(Receiver, gold: 100);
        fx.GiveMail(Receiver, gold: 5000);
        var svc = Svc(fx);
        var uid = fx.Mail.Mails.Single().Uid;

        await fx.AssertInvariantsAsync(async () =>
        {
            var (_, granted) = await svc.ClaimGoldAsync(Receiver, uid);
            player.Credit(granted);
            var (_, again) = await svc.ClaimGoldAsync(Receiver, uid);
            Assert.Equal(0, again);
        });
        Assert.Equal(5100, player.Gold);
    }

    // ---------------------------------------------------------------- E-H5-mail

    [Fact]
    public void MailAttachment_UntradeableOrBound_IsRefused()
    {
        var items = new Dictionary<uint, ItemData>
        {
            [1] = new() { ID = 1, Is_Trade = 1, Bind_Type = 0 },
            [2] = new() { ID = 2, Is_Trade = 0, Bind_Type = 0 },
            [3] = new() { ID = 3, Is_Trade = 1, Bind_Type = 2 },
        };

        Assert.False(MailSendHandler.RefusesAttachment(items, new InventoryItem { ItemId = 1 }));
        Assert.True(MailSendHandler.RefusesAttachment(items, new InventoryItem { ItemId = 2 }));      // Is_Trade 0
        Assert.True(MailSendHandler.RefusesAttachment(items, new InventoryItem { ItemId = 3 }));      // Bind_Type 2
        Assert.False(MailSendHandler.RefusesAttachment(items, new InventoryItem { ItemId = 99 }));    // unknown template
        Assert.False(MailSendHandler.RefusesAttachment(null, new InventoryItem { ItemId = 2 }));      // no data loaded
    }

    // ---------------------------------------------------------------- E-H8-mail-open

    // Wire layout of a 1394 body: compact prefix 01 0c, then the 12-byte
    // blob [u16 12][tag 0xE352][owner PC handle 0x06485A11][mailbox NPC handle 0x0001C013].
    private const string Retail1394Hex = "010c0c0052e3115a480613c00100";

    [Fact]
    public void MailboxOpen_ReadsTheNpcHandleAtBlobPlus8()
    {
        Assert.True(MailListHandler.TryReadNpcHandle(Convert.FromHexString(Retail1394Hex), out var handle));
        Assert.Equal(0x0001C013u, handle);
        Assert.Equal(NpcStateManager.NpcActorTag, (byte)(handle & 0xFF));

        Assert.False(MailListHandler.TryReadNpcHandle(Convert.FromHexString("010c"), out _));
        Assert.False(MailListHandler.TryReadNpcHandle(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void MailboxOpen_NeedsALivingPlayerOnTheNpcsMapInReach()
    {
        var npc = new NpcRuntimeState { MapId = 1100, PosX = 10, PosY = 10 };
        PlayerState Hero(uint map = 1100, float x = 10, float y = 10, bool dead = false) =>
            new() { MapId = map, X = x, Y = y, IsDead = dead };

        Assert.Equal(ActorRefusal.None, MailListHandler.Gate(Hero(), npc));
        Assert.Equal(ActorRefusal.TargetNotFound, MailListHandler.Gate(Hero(), null));
        Assert.Equal(ActorRefusal.CrossMap, MailListHandler.Gate(Hero(map: 1200), npc));
        Assert.Equal(ActorRefusal.ActorDead, MailListHandler.Gate(Hero(dead: true), npc));
        Assert.Equal(ActorRefusal.OutOfRange, MailListHandler.Gate(Hero(x: 10 + ActorGate.NpcInteractReach + 1), npc));
    }

    // ---------------------------------------------------------------- E-H12: cash-mall caps

    private const string Retail1598BuyHex =
        "3600000001002c2d2c04e30b00000000f0410100000000000000f041040031003200330034000000000000000000000000000000000000000000";

    /// <summary>A valid buy 1598 with its single product line repeated <paramref name="n"/> times (n announced as u16).</summary>
    private static byte[] BuyWithLines(int n)
    {
        var body = Convert.FromHexString(Retail1598BuyHex);
        var entry = body.AsSpan(6, GameMallPurchaseHandler.ProductEntryBytes);
        var tail = body.AsSpan(6 + GameMallPurchaseHandler.ProductEntryBytes);
        var blob = new byte[2 + n * GameMallPurchaseHandler.ProductEntryBytes + tail.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(blob, (ushort)n);
        for (int i = 0; i < n; i++) entry.CopyTo(blob.AsSpan(2 + i * GameMallPurchaseHandler.ProductEntryBytes));
        tail.CopyTo(blob.AsSpan(2 + n * GameMallPurchaseHandler.ProductEntryBytes));
        var framed = new byte[4 + blob.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)blob.Length);
        blob.CopyTo(framed, 4);
        return framed;
    }

    [Fact]
    public void Purchase_OversizedLineCountIsRefused_ButTheRetailShapeParses()
    {
        Assert.True(GameMallPurchaseHandler.TryParse(BuyWithLines(1), out _));          // a normal buy has n = 1
        Assert.False(GameMallPurchaseHandler.TryParse(BuyWithLines(2), out _));
        Assert.False(GameMallPurchaseHandler.TryParse(BuyWithLines(ushort.MaxValue), out _));  // n = 65535, a full body
    }

    [Fact]
    public void Purchase_QuantityAboveTheCap_IsRefused()
    {
        var catalog = CashMallCatalog.Load(Packets.GameMall2866And2868Tests.CatalogPath());
        long now = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.True(GameMallPurchaseHandler.TryParse(Convert.FromHexString(Retail1598BuyHex), out var buy));
        var line = buy.Lines[0];

        var huge = buy with { Lines = new[] { line with { Quantity = ushort.MaxValue } } };
        Assert.Equal(GameMallPurchaseHandler.CodeNotOffered, GameMallPurchaseHandler.Validate(huge, catalog, now, out var priced));
        Assert.Empty(priced);

        var two = buy with { Lines = new[] { line, line } };
        Assert.Equal(GameMallPurchaseHandler.CodeNotOffered, GameMallPurchaseHandler.Validate(two, catalog, now, out _));

        // The original request still validates.
        Assert.Equal(0, GameMallPurchaseHandler.Validate(buy, catalog, now, out _));
        Assert.Equal(1, GameMallPurchaseHandler.EntriesFor((ushort)GameMallPurchaseHandler.MaxQuantityPerLine));
    }

    // ---------------------------------------------------------------- E-H12: gift password lockout

    private sealed class CountingPasswords(bool hasPassword, string correct) : IPresentPasswordRepository
    {
        public int Verifications;
        public Task<bool> HasPasswordAsync(uint accountId, CancellationToken ct = default) => Task.FromResult(hasPassword);
        public Task<bool> VerifyAsync(uint accountId, string password, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Verifications);
            return Task.FromResult(password == correct);
        }
        public Task<bool> SetAsync(uint accountId, string oldPassword, string newPassword, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    [Fact]
    public async Task ThousandWrongGiftPasswords_LockTheAccountOutAfterK()
    {
        var gate = new GiftPasswordGate();
        var store = new CountingPasswords(true, "1234");
        int accepted = 0;
        for (int i = 0; i < 1000; i++)
            if (await gate.CheckAsync(store, 7, $"{i:D4}x", CancellationToken.None)) accepted++;

        Assert.Equal(0, accepted);
        Assert.Equal(GiftPasswordGate.MaxAttemptsPerAccount, store.Verifications);   // the other 990 never reached the store
        Assert.False(await gate.CheckAsync(store, 7, "1234", CancellationToken.None)); // locked out, even for the right one
        Assert.Equal(GiftPasswordGate.MaxAttemptsPerAccount, store.Verifications);
        Assert.True(await gate.CheckAsync(store, 8, "1234", CancellationToken.None));  // another account is untouched
    }

    [Fact]
    public async Task CorrectGiftPassword_ClearsTheBudget_AndNoPasswordIsNeverThrottled()
    {
        var gate = new GiftPasswordGate();
        var store = new CountingPasswords(true, "1234");
        for (int i = 0; i < GiftPasswordGate.MaxAttemptsPerAccount - 1; i++)
            Assert.False(await gate.CheckAsync(store, 7, "0000", CancellationToken.None));
        Assert.True(await gate.CheckAsync(store, 7, "1234", CancellationToken.None));   // the 10th, and a hit: resets
        for (int i = 0; i < GiftPasswordGate.MaxAttemptsPerAccount; i++)
            Assert.False(await gate.CheckAsync(store, 7, "0000", CancellationToken.None));
        Assert.Equal(2 * GiftPasswordGate.MaxAttemptsPerAccount, store.Verifications);

        var none = new CountingPasswords(false, "");
        for (int i = 0; i < 100; i++) Assert.True(await gate.CheckAsync(none, 9, "anything", CancellationToken.None));
        Assert.Equal(0, none.Verifications);
    }

    // ---------------------------------------------------------------- E-H12: find-char throttle

    [Fact]
    public void FindChar_IsThrottledPerSession()
    {
        const uint session = 0xFA110001, other = 0xFA110002;
        for (int i = 0; i < GameMallFindCharHandler.MaxLookupsPerSession; i++)
            Assert.True(GameMallFindCharHandler.TryAdmitLookup(session));
        Assert.False(GameMallFindCharHandler.TryAdmitLookup(session));
        Assert.True(GameMallFindCharHandler.TryAdmitLookup(other));
    }

    // ---------------------------------------------------------------- fault injection

    /// <summary>Forwards every call to the real repository; the named method runs on it and THEN throws, so the write
    /// has happened when the caller sees the failure (a fault after the placement write).</summary>
    private class FaultAfter : DispatchProxy
    {
        private IInventoryRepository _inner = null!;
        private string _method = "";
        private bool _fired;

        public static IInventoryRepository Wrap(IInventoryRepository inner, string method)
        {
            var proxy = Create<IInventoryRepository, FaultAfter>();
            var self = (FaultAfter)(object)proxy;
            self._inner = inner;
            self._method = method;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result;
            try { result = targetMethod!.Invoke(_inner, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }

            // The first positive-delta AdjustCount / the first Add is the placement; later calls (the undo) pass through.
            bool placement = targetMethod.Name == _method && !_fired
                             && !(targetMethod.Name == nameof(IInventoryRepository.AdjustCountAsync) && (int)args![2]! < 0);
            if (!placement) return result;
            _fired = true;
            return result is Task<int?> counted ? FaultCounted(counted) : Fault((Task)result!);
        }

        private static async Task<int?> FaultCounted(Task<int?> done)
        {
            await done;
            throw new IOException("injected fault after the placement write");
        }

        private static async Task Fault(Task done)
        {
            await done;
            throw new IOException("injected fault after the placement write");
        }
    }
}
