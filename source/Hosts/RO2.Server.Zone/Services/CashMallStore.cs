using RO2.Domain.Entities;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Zone-side constants and helpers for the per-account cash mall. Persistence goes through <see cref="RO2.Application.Interfaces.ICashMallRepository"/>; box filtering and counting are in <see cref="CashMallRules"/>.
/// </summary>
public static class CashMallStore
{
 /// <summary>Box id for items the account bought for itself.</summary>
    public const byte BoxPurchased = CashMallRules.BoxPurchased;

 /// <summary>Box id for gifts that other accounts bought for this one.</summary>
    public const byte BoxGift = CashMallRules.BoxGift;

 /// <summary>Counter name used to generate <see cref="CashMallBoxEntry.EntryId"/> values.</summary>
    public const string EntryCounter = "entry";

 /// <summary>Counter name used to generate <see cref="CashMallPurchase.OrderId"/> values.</summary>
    public const string OrderCounter = "order";

 /// <summary>
 /// Converts a stored UTC instant to the server's local clock, which is the clock the wire protocol carries (<see cref="ServerWallClock"/>).
 /// </summary>
    public static DateTime ToLocal(DateTime storedUtc) => DateTime.SpecifyKind(storedUtc, DateTimeKind.Utc).ToLocalTime();
}
