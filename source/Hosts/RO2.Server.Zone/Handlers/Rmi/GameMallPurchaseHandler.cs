using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1598 ReqGameMallPurchase - Buy (or gift) cash-mall products</summary>
/// <remarks>
/// <para>
/// Request wire layout, U32 envelope <c>[u32 blobLen][blob]</c>, blob = <c>[u16 n][n x 22-byte entry][WStr16 password]
/// [u64 sender GID][u32 recipient account][WStr16 message][WStr16 sender name][WStr16 recipient name][u16 0]</c>,
/// WStr16 = <c>[u16 chars][2 x chars bytes]</c>. Entry = <c>[u32 itemId][u32 productId][f32 total][u16 quantity]
/// [u32 0][f32 total]</c>: the f32s are the line's TOTAL, not the unit price (two units of a 30.0 product carry 60.0).
/// A plain purchase leaves GID, account and the three strings empty; a gift fills them, the account being the one
/// 2876 AnsGameMallFindChar returned. The client sends one 1598 per product (n = 1).
/// </para>
/// <para>
/// Validation: the request must name a catalog product, with total =
/// <see cref="CashMallCatalog.Product.UnitPrice"/> x quantity. An unknown product is refused with 3010, a total that
/// does not match the price with 24211 "total amount does not equal the price". The product is
/// found by its item and the total checked against it; <see cref="Validate"/> does that, then checks the product id
/// and the sale window. A gift is also checked for its receiver (24208) and the account's gift password (24206).
/// </para>
/// <para>
/// Then the balance is debited in one conditional write that fails when it is short (<see cref="ICashMallRepository.TryPurchaseAsync"/>),
/// the purchase goes in the history (2874), and each product becomes an entry of the web-mall box: box 1 of the
/// buyer's account for a purchase, box 2 of the recipient's account for a gift. The answer is the 26-byte 2870
/// success with the new balance (<see cref="CashMallPackets.Build2870Success"/>), and 2638 then tells every online
/// session of the account whose box changed how many entries wait.
/// </para>
/// </remarks>
public class GameMallPurchaseHandler : ZoneRmiHandlerBase<GameMallPurchaseHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGameMallPurchase;
    public override string Name => "GameMallPurchase";
    public GameMallPurchaseHandler(IServiceScopeFactory s, ILogger<GameMallPurchaseHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>
    /// The balance is short. 3001 is the billing code of string_ui 3001 "Not enough Hangame Kafra Points.", the family
    /// of 3010 "Not an existing service."; the client shows it as string 10942 "Error!!(Error code:3001)".
    /// </summary>
    internal const ushort CodeNotEnoughCash = 3001;

    /// <summary>The product is not one this server sells, or a
    /// server fault kept the purchase from going through (the client shows 10942 "Error!!(Error code:3010) Please try
    /// again after a while.").</summary>
    internal const ushort CodeNotOffered = 3010;

    /// <summary>24204 -> string 10914, the product's sale period is over.</summary>
    internal const ushort CodeSalePeriodOver = 24204;

    /// <summary>24206 -> string 10926 "Incorrect Password.": the gift password does not match the account's.</summary>
    internal const ushort CodeWrongPassword = 24206;

    /// <summary>24208 -> string 10856 "We couldn't find the receiver from the server.".</summary>
    internal const ushort CodeReceiverNotFound = 24208;

    /// <summary>24211 -> string 10985 "Total amound does not equal the price of the item that you want to purchase."</summary>
    internal const ushort CodeTotalMismatch = 24211;

    /// <summary>Bytes of one product entry of the request.</summary>
    internal const int ProductEntryBytes = 22;

    /// <summary>A box entry's count is a u8 on the wire (2639/2640 +4), so a larger quantity is filed as several entries.</summary>
    internal const int MaxUnitsPerEntry = byte.MaxValue;

    /// <summary>
    /// Product lines one 1598 may carry. The client sends one 1598 per product (n = 1), so a request naming more is
    /// refused instead of being parsed into up to 65535 lines.
    /// </summary>
    internal const int MaxLinesPerRequest = 1;

    /// <summary>
    /// Units one line may buy. A box entry's count is a u8 on the wire (<see cref="MaxUnitsPerEntry"/>), so a line
    /// stays one entry; the u16 quantity otherwise reached 65535 units = 257 entries per line. This is a defensive bound.
    /// </summary>
    internal const int MaxQuantityPerLine = MaxUnitsPerEntry;

    /// <summary>One product line of a 1598.</summary>
    internal readonly record struct Line(uint ItemId, uint ProductId, float Total, ushort Quantity);

    /// <summary>A parsed 1598.</summary>
    internal sealed record Request(IReadOnlyList<Line> Lines, string Password, ulong SenderGid, uint RecipientAccount,
        string Message, string SenderName, string RecipientName)
    {
        public bool IsGift => RecipientAccount != 0;
    }

    /// <summary>A line that passed <see cref="Validate"/>, with the product it buys.</summary>
    internal readonly record struct PricedLine(CashMallCatalog.Product Product, ushort Quantity)
    {
        public long Total => Product.UnitPrice * Quantity;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var req))
        {
            Logger.LogDebug("{Name}: 1598 of {Len} B does not match the purchase layout, refused", player.Name, payload.Length);
            await RefuseAsync(session, CodeNotOffered, gift: false, ct);
            return;
        }

        var code = Validate(req, CashMallCatalog.Shipped, ServerWallClock.Now().ToUnixTimeSeconds(), out var priced);
        if (code == 0 && req.IsGift)
            code = await CheckGiftAsync(services.GetRequiredService<IPresentPasswordRepository>(), services.GetRequiredService<ICharacterRepository>(), player, req, ct);
        if (code != 0 || player.AccountId == 0)
        {
            if (code == 0) code = CodeNotOffered; // no account to charge
            Logger.LogInformation("{Name} tries to {Kind} game mall item {Item} (product {Product}): refused with {Code}",
                player.Name, req.IsGift ? $"gift account {req.RecipientAccount}" : "buy",
                req.Lines.Count > 0 ? req.Lines[0].ItemId : 0, req.Lines.Count > 0 ? req.Lines[0].ProductId : 0, code);
            await RefuseAsync(session, code, req.IsGift, ct);
            return;
        }

        if (req.IsGift && !string.Equals(req.SenderName, player.Name, StringComparison.Ordinal))
            Logger.LogDebug("{Name}: 1598 names sender '{Sender}'; the gift is filed under the character's own name",
                player.Name, req.SenderName);

        var store = services.GetRequiredService<ICashMallRepository>();
        long total = priced.Sum(p => p.Total);
        uint boxAccount = req.IsGift ? req.RecipientAccount : player.AccountId;
        byte box = req.IsGift ? CashMallStore.BoxGift : CashMallStore.BoxPurchased;
        var nowUtc = DateTime.UtcNow;

        uint[] orderIds, entryIds;
        long? balance;
        List<CashMallBoxEntry> entries;
        try
        {
            orderIds = await store.ReserveIdsAsync(CashMallStore.OrderCounter, priced.Count, ct);
            entryIds = await store.ReserveIdsAsync(CashMallStore.EntryCounter, priced.Sum(p => EntriesFor(p.Quantity)), ct);
            entries = BuildEntries(priced, entryIds, box, nowUtc, req.IsGift ? player.Name : "");
            var log = priced.Select((p, i) => new CashMallPurchase
            {
                OrderId = orderIds[i],
                ItemId = p.Product.ItemId,
                ProductId = p.Product.ProductId,
                Quantity = p.Quantity,
                UnitPrice = p.Product.UnitPrice,
                BoughtUtc = nowUtc,
                Gift = req.IsGift,
                SenderName = req.IsGift ? CashMallPackets.ClipName(player.Name) : "",
                RecipientName = req.IsGift ? CashMallPackets.ClipName(req.RecipientName) : "",
                RecipientAccountId = req.RecipientAccount,
            }).ToList();
            balance = await store.TryPurchaseAsync(player.AccountId, total, log,
                req.IsGift ? Array.Empty<CashMallBoxEntry>() : entries, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "{Name}: game mall purchase could not be stored; nothing charged", player.Name);
            await RefuseAsync(session, CodeNotOffered, req.IsGift, ct);
            return;
        }

        if (balance is null)
        {
            Logger.LogInformation("{Name} (account {Account}) cannot pay {Total} cash for item {Item}: refused with {Code}",
                player.Name, player.AccountId, total, priced[0].Product.ItemId, CodeNotEnoughCash);
            await RefuseAsync(session, CodeNotEnoughCash, req.IsGift, ct);
            return;
        }

        if (req.IsGift)
        {
            try
            {
                await store.FileGiftAsync(req.RecipientAccount, entries, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The gift never reached the recipient: give the cash back rather than charge for nothing.
                Logger.LogWarning(ex, "{Name}: gift for account {Account} could not be filed; refunding {Total} cash",
                    player.Name, req.RecipientAccount, total);
                try { await store.RefundAsync(player.AccountId, total, orderIds, Array.Empty<uint>(), ct); }
                catch (Exception rex) when (rex is not OperationCanceledException)
                {
                    Logger.LogError(rex, "{Name}: refund of {Total} cash to account {Account} failed", player.Name, total, player.AccountId);
                }
                await RefuseAsync(session, CodeNotOffered, gift: true, ct);
                return;
            }
        }

        var delivered = priced.Select((p, i) => new CashMallPackets.Delivered(p.Product.ItemId, p.Quantity, orderIds[i])).ToList();
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGameMallPurchase,
            CashMallPackets.Build2870Success(delivered, balance.Value, req.IsGift), ct);
        Logger.LogInformation("{Name} (account {Account}) {Kind} {Lines} for {Total} cash; balance now {Balance}",
            player.Name, player.AccountId, req.IsGift ? $"gifted account {req.RecipientAccount}" : "bought",
            string.Join(", ", priced.Select(p => $"{p.Quantity}x {p.Product.ItemId}")), total, balance.Value);

        await NotifyBoxAsync(store, boxAccount, ct);
    }

    /// <summary>
    /// Sends 2638 with <paramref name="accountId"/>'s box counts to every online session of that account: the buyer
    /// after a purchase, the recipient after a gift (the buyer too when it gifted its own account).
    /// </summary>
    private async Task NotifyBoxAsync(ICashMallRepository store, uint accountId, CancellationToken ct)
    {
        var online = PlayerManager.GetAllPlayers().Where(p => p.AccountId == accountId).ToList();
        if (online.Count == 0) return;
        (int Purchased, int Gifts) counts;
        try { counts = await store.GetBoxCountsAsync(accountId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Web mall box counts of account {Account} could not be read; no 2638 sent", accountId);
            return;
        }
        var body = WebMallNotifyPackets.Build2638(counts.Purchased, counts.Gifts);
        foreach (var p in online)
            if (Broadcast.GetSession(p.SessionId) is { } s)
                await s.SendRmiAsync(ZoneS2COpcodes.NfyWebMallItems, body, ct);
    }

    /// <summary>
    /// The receiver must be an account with a character (24208), and the buyer's gift password must match when the
    /// account has one (24206), the same rule 1605 applies (CheckPresentPasswordHandler): an account with no password
    /// has nothing to fail against, and a store failure counts as a mismatch so an outage never opens the gift path.
    /// </summary>
    private async Task<ushort> CheckGiftAsync(IPresentPasswordRepository passwords, ICharacterRepository characters, PlayerState player, Request req, CancellationToken ct)
    {
        try
        {
            var hasCharacter = await characters.AccountHasLiveCharacterAsync(req.RecipientAccount, ct);
            if (!hasCharacter) return CodeReceiverNotFound;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "{Name}: gift receiver account {Account} could not be looked up", player.Name, req.RecipientAccount);
            return CodeReceiverNotFound;
        }

        try
        {
            // The guesses are throttled per account, shared with 1605, so a locked-out account is refused
            // here without a verification being run.
            bool ok = await GiftPasswordGate.Shared.CheckAsync(passwords, player.AccountId, req.Password, ct);
            return ok ? (ushort)0 : CodeWrongPassword;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "{Name}: gift password check failed for account {Account}", player.Name, player.AccountId);
            return CodeWrongPassword;
        }
    }

    private static Task RefuseAsync(ProudSession session, ushort code, bool gift, CancellationToken ct)
        => session.SendRmiAsync(ZoneS2COpcodes.AnsGameMallPurchase, CashMallPackets.Build2870Refusal(code, gift), ct);

    /// <summary>
    /// The catalog checks of a purchase, in order: every line must name an item of
    /// the catalog (3010) with a total of exactly its unit price times a quantity of at least 1 (24211), its product id
    /// (3010) and a sale window that contains <paramref name="nowUnix"/> (24204). 0 when all lines pass.
    /// </summary>
    internal static ushort Validate(Request req, CashMallCatalog? catalog, long nowUnix, out List<PricedLine> priced)
    {
        priced = new List<PricedLine>(Math.Min(req.Lines.Count, MaxLinesPerRequest));
        if (catalog is null || req.Lines.Count == 0 || req.Lines.Count > MaxLinesPerRequest) return CodeNotOffered;
        foreach (var line in req.Lines)
        {
            var product = catalog.FindByItem(line.ItemId);
            if (product is null) return CodeNotOffered;
            // Defensive refusal code: 3010 is the one code the client already renders for a
            // product this server does not sell.
            if (line.Quantity > MaxQuantityPerLine) return CodeNotOffered;
            if (line.Quantity == 0 || (float)(product.UnitPrice * line.Quantity) != line.Total) return CodeTotalMismatch;
            if (line.ProductId != product.ProductId) return CodeNotOffered;
            if (!product.OnSaleAt(nowUnix)) return CodeSalePeriodOver;
            priced.Add(new PricedLine(product, line.Quantity));
        }
        return 0;
    }

    /// <summary>How many box entries <paramref name="quantity"/> units need (<see cref="MaxUnitsPerEntry"/> each).</summary>
    internal static int EntriesFor(ushort quantity) => Math.Max(1, (quantity + MaxUnitsPerEntry - 1) / MaxUnitsPerEntry);

    /// <summary>The box entries of a purchase: one per line, split at <see cref="MaxUnitsPerEntry"/> units, taking
    /// the ids of <paramref name="entryIds"/> in order.</summary>
    internal static List<CashMallBoxEntry> BuildEntries(IReadOnlyList<PricedLine> priced, IReadOnlyList<uint> entryIds, byte box,
        DateTime nowUtc, string senderName)
    {
        var entries = new List<CashMallBoxEntry>(entryIds.Count);
        int next = 0;
        foreach (var p in priced)
        {
            for (int left = p.Quantity; left > 0;)
            {
                int units = Math.Min(left, MaxUnitsPerEntry);
                entries.Add(new CashMallBoxEntry
                {
                    EntryId = entryIds[next++],
                    Box = box,
                    ItemId = p.Product.ItemId,
                    Count = units,
                    FiledUtc = nowUtc,
                    SenderName = senderName,
                });
                left -= units;
            }
        }
        return entries;
    }

    /// <summary>Reads a 1598 body (layout in the class remarks). False when it does not match.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> body, out Request request)
    {
        request = null!;
        if (body.Length < 4) return false;
        long blobLen = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (blobLen != body.Length - 4) return false;
        var blob = body[4..];

        if (blob.Length < 2) return false;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(blob);
        int pos = 2;
        if (n < 1 || n > MaxLinesPerRequest || pos + n * ProductEntryBytes > blob.Length) return false;
        var lines = new List<Line>(n);
        for (int i = 0; i < n; i++, pos += ProductEntryBytes)
        {
            var e = blob.Slice(pos, ProductEntryBytes);
            lines.Add(new Line(
                BinaryPrimitives.ReadUInt32LittleEndian(e),
                BinaryPrimitives.ReadUInt32LittleEndian(e[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(e[8..]),
                BinaryPrimitives.ReadUInt16LittleEndian(e[12..])));
        }

        if (!TryReadWStr16(blob, ref pos, out var password)) return false;
        if (pos + 12 > blob.Length) return false;
        ulong gid = BinaryPrimitives.ReadUInt64LittleEndian(blob[pos..]);
        uint account = BinaryPrimitives.ReadUInt32LittleEndian(blob[(pos + 8)..]);
        pos += 12;
        if (!TryReadWStr16(blob, ref pos, out var message)) return false;
        if (!TryReadWStr16(blob, ref pos, out var sender)) return false;
        if (!TryReadWStr16(blob, ref pos, out var recipient)) return false;

        request = new Request(lines, password, gid, account, message, sender, recipient);
        return true;
    }

    /// <summary>A <c>[u16 chars][2 x chars bytes]</c> UTF-16 string.</summary>
    private static bool TryReadWStr16(ReadOnlySpan<byte> b, ref int pos, out string value)
    {
        value = "";
        if (pos + 2 > b.Length) return false;
        int chars = BinaryPrimitives.ReadUInt16LittleEndian(b[pos..]);
        if (pos + 2 + 2 * chars > b.Length) return false;
        value = Encoding.Unicode.GetString(b.Slice(pos + 2, 2 * chars));
        pos += 2 + 2 * chars;
        return true;
    }
}
