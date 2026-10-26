using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Zone-side rules of the account gift box: which gifts are listed, counted, and claimed. The box is shared by
/// all characters of a login account. Persistence is handled by <see cref="IAccountGiftRepository"/>; this class
/// holds the filing and paging helpers and forwards expiry rules to <see cref="AccountGiftRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// Gifts are filed by the play-time reward and the daily attendance reward.
/// </para>
/// <para>
/// A gift starts with 7 days left on the day it is filed, loses one day per server day, and is dropped the day
/// after it reaches 0. Expired gifts are not listed, counted, or claimed.
/// </para>
/// </remarks>
public static class AccountGiftStore
{
 /// <summary>Source code of a play-time reward gift.</summary>
    public const byte SourcePlayTime = 1;

 /// <summary>Days a gift stays in the box, counting the day it was filed.</summary>
    internal const int DaysKept = AccountGiftRules.DaysKept;

 /// <summary>
 /// Page window: page P lists the live gifts, newest first, starting at index <c>5 * (P - 1)</c> (floored at 0)
 /// and holding up to 7 entries. Consecutive pages share two gifts.
 /// </summary>
    internal const int PageStride = 5, PageRows = 7;

 /// <summary>Creates a gift filed at the given time, with a new random 16-byte ID.</summary>
    public static AccountGift NewGift(uint itemId, ushort count, byte source, DateTime nowUtc) => new()
    {
        Guid = Convert.ToHexString(System.Guid.NewGuid().ToByteArray()),
        ItemId = itemId,
        Count = count,
        ReceivedUtc = nowUtc,
        Source = source,
    };

 /// <summary>Days left for a gift filed at <paramref name="receivedLocal"/>: 7 on that day, 0 seven days later.</summary>
    public static int DaysLeft(DateTime receivedLocal, DateTime nowLocal)
        => AccountGiftRules.DaysLeft(receivedLocal, nowLocal);

 /// <summary>Whether a gift is still in the box (its days left are not below 0).</summary>
    public static bool IsLive(AccountGift gift, DateTime nowLocal)
        => AccountGiftRules.IsLive(gift, nowLocal);

 /// <summary>The live gifts of <paramref name="gifts"/>, newest first.</summary>
    public static List<AccountGift> NewestFirst(IEnumerable<AccountGift> gifts, DateTime nowLocal)
        => AccountGiftRules.NewestFirst(gifts, nowLocal);

 /// <summary>
 /// The first index and count of the gifts that page <paramref name="page"/> lists out of <paramref name="total"/>.
 /// </summary>
    public static (int Start, int Count) PageWindow(int total, ushort page)
    {
        int from = PageStride * (page - 1);
        int to = Math.Min(total, from + PageRows);
        from = Math.Max(0, from);
        return (from, Math.Max(0, to - from));
    }

 /// <summary>Converts a stored UTC instant to the server's local clock, the one the wire carries.</summary>
    public static DateTime ToLocal(DateTime storedUtc) => AccountGiftRules.ToLocal(storedUtc);
}

/// <summary>
/// Handles RMI 1551 (ReqGetAccountGift): takes one gift out of the account's box into the claiming character's
/// bag, answered with 2800 AnsGetAccountGift.
/// </summary>
/// <remarks>
/// <para>
/// Request: <c>[u32 31][u32 account][u8][u64 character GID][u16][16-byte GUID]</c>.
/// </para>
/// <para>
/// Answer: result 0 with the bag record of the stack the gift went into, or result 3 with a zeroed record when
/// the bag is full. A GUID that the box does not hold gets no answer.
/// </para>
/// </remarks>
public static class AccountGiftClaims
{
    private const int RequestLength = 35;
    private const int GidOffset = 9;
    private const int GuidOffset = 19;
    private const int GuidBytes = 16;

    public static async Task HandleAsync(ProudSession session, PlayerState player, ReadOnlyMemory<byte> payload,
        IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        if (payload.Length < RequestLength || player.AccountId == 0)
        {
            logger.LogDebug("{Name}: 1551 of {Len} B (account {Account}), not answered", player.Name, payload.Length, player.AccountId);
            return;
        }
        var span = payload.Span;
        ulong gid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(GidOffset, 8));
        var guidBytes = span.Slice(GuidOffset, GuidBytes).ToArray();
        string guid = Convert.ToHexString(guidBytes);

        var nowLocal = ServerWallClock.Now().DateTime;
        var giftStore = services.GetRequiredService<IAccountGiftRepository>();
        AccountGift? gift;
        try
        {
            gift = (await giftStore.GetLiveAsync(player.AccountId, nowLocal, ct)).FirstOrDefault(g => g.Guid == guid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: 1551 for gift {Guid} not answered, the gift box could not be read", player.Name, guid);
            return;
        }
        if (gift is null)
        {
            logger.LogDebug("{Name}: 1551 for gift {Guid}, which is not in account {Account}'s box: not answered",
                player.Name, guid, player.AccountId);
            return;
        }

        var store = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var owned = await inventory.GetByCharacterAsync(player.CharacterId);
        var landing = ItemPickUpHandler.PlanLanding(owned, player.CharacterId, gift.ItemId, Math.Max(1, (int)gift.Count), store.Items);
        if (landing.Refused)
        {
            
            await session.SendRmiAsync(ZoneS2COpcodes.AnsGetAccountGift, AccountGiftPackets.Build2800(player.AccountId,
                AccountGiftPackets.ClaimBagFull, gid, guidBytes, gift.ItemId, gift.Count, gift.Source, record126: null), ct);
            logger.LogInformation("{Name}: gift {Guid} ({Count}x {Item}) stays in the box, the bag is full",
                player.Name, guid, gift.Count, gift.ItemId);
            return;
        }

        if (!await giftStore.TryTakeAsync(player.AccountId, guid, ct))
            return;   

        var landed = await ItemPickUpHandler.WriteLandingAsync(inventory, landing, player, gift.ItemId, logger);
        if (landed.Count == 0)
        {
            
            await giftStore.FileAsync(player.AccountId, new[] { gift }, nowLocal, ct);
            return;
        }

        
        
        await services.GetRequiredService<LootService>().AdvanceCollectQuestsAsync(session, player, gift.ItemId, gift.Count);
        await KharaService.OnHeldItemChangedAsync(session, player, gift.ItemId, services, logger);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGetAccountGift, AccountGiftPackets.Build2800(player.AccountId,
            AccountGiftPackets.ClaimOk, gid, guidBytes, gift.ItemId, gift.Count, gift.Source,
            ItemPackets.BuildItemRecord126(landed[0])), ct);
        logger.LogInformation("{Name} took gift {Guid} ({Count}x {Item}) from the gift box", player.Name, guid, gift.Count, gift.ItemId);

        await CardRegistrationNotifier.NotifyArrivedAsync(session, player.CharacterId, new[] { gift.ItemId }, services, logger);
    }
}
