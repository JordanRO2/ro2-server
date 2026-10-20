using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Per-account cash-mall persistence: the balance (2868), the web-mall storage box (2638/1471/1472) and the
/// purchase history (1594/2874), over the <c>cash_mall_accounts</c> collection, plus the two private id sequences
/// in <c>cash_mall_counters</c>. The wire builders and the box paging/OLE-date rules stay in the Zone host;
/// the box filter/sort/counts are CashMallRules. tools/grant_mall_cash.py is a second, out-of-game writer to
/// <c>cash_mall_accounts</c> and is not part of this contract.
/// </summary>
public interface ICashMallRepository
{
 /// <summary>The account's balance; 0 for an account with no cash-mall document.</summary>
    Task<long> GetBalanceAsync(uint accountId, CancellationToken ct = default);

 /// <summary>Entries of <paramref name="box"/> that still hold units, newest first.</summary>
    Task<IReadOnlyList<CashMallBoxEntry>> GetBoxAsync(uint accountId, byte box, CancellationToken ct = default);

 /// <summary>The box entry <paramref name="entryId"/> (either box) while it still holds units, else null.</summary>
    Task<CashMallBoxEntry?> GetBoxEntryAsync(uint accountId, uint entryId, CancellationToken ct = default);

 /// <summary>How many entries wait in box 1 and in box 2: the two counts 2638 carries.</summary>
    Task<(int Purchased, int Gifts)> GetBoxCountsAsync(uint accountId, CancellationToken ct = default);

 /// <summary>The account's purchase history, oldest first; empty for an account with no cash-mall document.</summary>
    Task<IReadOnlyList<CashMallPurchase>> GetPurchaseHistoryAsync(uint accountId, CancellationToken ct = default);

 /// <summary><paramref name="count"/> consecutive ids from the server-wide sequence <paramref name="counter"/>
 /// (upserted on first use, so the first id is 1).</summary>
    Task<uint[]> ReserveIdsAsync(string counter, int count, CancellationToken ct = default);

 /// <summary>The debit of a purchase in one conditional write (only while the balance is at least
 /// <paramref name="total"/>): takes the total off, appends <paramref name="log"/> and files
 /// <paramref name="ownEntries"/>. The balance after it, or null when the account cannot pay (nothing changed).</summary>
    Task<long?> TryPurchaseAsync(uint accountId, long total, IReadOnlyList<CashMallPurchase> log,
        IReadOnlyList<CashMallBoxEntry> ownEntries, CancellationToken ct = default);

 /// <summary>Undoes a <see cref="TryPurchaseAsync"/> whose delivery failed: gives <paramref name="total"/> back and
 /// drops the purchases (<paramref name="orderIds"/>) and own-box entries (<paramref name="entryIds"/>) it wrote.</summary>
    Task RefundAsync(uint accountId, long total, uint[] orderIds, uint[] entryIds, CancellationToken ct = default);

 /// <summary>Adds <paramref name="entries"/> to the account's box (one atomic push, creating the document when the
 /// account has none): where a gift goes.</summary>
    Task FileGiftAsync(uint accountId, IReadOnlyList<CashMallBoxEntry> entries, CancellationToken ct = default);

 /// <summary>Takes <paramref name="units"/> units out of entry <paramref name="entryId"/> only while it still holds
 /// that many, and drops the entry once it is empty. The entry as it stood before, or null when it is not in the
 /// box or holds fewer units.</summary>
    Task<CashMallBoxEntry?> TryTakeUnitsAsync(uint accountId, uint entryId, int units, CancellationToken ct = default);

 /// <summary>Puts <paramref name="units"/> units of a taken entry back: into the entry when still there, else as
 /// the entry again.</summary>
    Task ReturnUnitsAsync(uint accountId, CashMallBoxEntry taken, int units, CancellationToken ct = default);
}
