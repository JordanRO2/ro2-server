using RO2.Network.Packets;
using RO2.Domain.Entities;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Body builders for the account gift box packets: 2798 NfyAccountGiftReceived, 2799 NfyAccountGiftInfo,
/// 2800 AnsGetAccountGift, and 2821 NfyAccountGiftCount. The box itself is <see cref="AccountGiftStore"/>.
/// </summary>
public static class AccountGiftPackets
{
 /// <summary>Claim result: the gift went into the bag.</summary>
    public const uint ClaimOk = 0;

 /// <summary>Claim result: the bag has no room for the gift.</summary>
    public const uint ClaimBagFull = 3;

 /// <summary>Length in bytes of one gift record in a 2799 list.</summary>
    internal const int GiftRecordLength = 43;

 /// <summary>Builds RMI 2798 NfyAccountGiftReceived, sent after a gift is filed: <c>[u32 gifts in the box]</c>.</summary>
    public static byte[] Build2798(int giftsInBox)
    {
        var body = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)Math.Max(0, giftsInBox));
        return body;
    }

 /// <summary>
 /// Builds RMI 2821 NfyAccountGiftCount: <c>[u16 gifts in the box]</c>, sent at the end of a map load. Zero is the
 /// empty-box form.
 /// </summary>
    public static byte[] Build2821(int giftsInBox)
    {
        var body = new byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)Math.Clamp(giftsInBox, 0, ushort.MaxValue));
        return body;
    }

 /// <summary>
 /// Builds RMI 2799 NfyAccountGiftInfo, one page of the account's live gifts:
 /// <c>[u32 18 + 43n][u32 accountId][u64 character GID][u16 page][u16 total][u16 n][n gift records]</c>.
 /// </summary>
 /// <remarks>
 /// The page window is <see cref="AccountGiftStore.PageWindow"/>. Each record is written by <see cref="WriteGiftRecord"/>.
 /// </remarks>
    public static byte[] Build2799(uint accountId, ulong characterGid, ushort page, IReadOnlyList<AccountGift> newestFirst,
        DateTime nowLocal)
    {
        var (start, count) = AccountGiftStore.PageWindow(newestFirst.Count, page);
        using var w = new PacketWriter(4 + 18 + GiftRecordLength * count);
        w.WriteUInt32((uint)(18 + GiftRecordLength * count));
        w.WriteUInt32(accountId);
        w.WriteUInt64(characterGid);
        w.WriteUInt16(page);
        w.WriteUInt16((ushort)Math.Clamp(newestFirst.Count, 0, ushort.MaxValue));
        w.WriteUInt16((ushort)count);
        for (int i = start; i < start + count; i++)
            WriteGiftRecord(w, newestFirst[i], nowLocal);
        return w.ToArray();
    }

 /// <summary>
 /// Writes one 43-byte gift record: GUID, item ID, count, received time, received time floored to 45 minutes,
 /// days left, source, and padding.
 /// </summary>
 /// <remarks>
 /// Both times are OLE dates on the server's local clock.
 /// </remarks>
    internal static void WriteGiftRecord(PacketWriter w, AccountGift gift, DateTime nowLocal)
    {
        var received = AccountGiftStore.ToLocal(gift.ReceivedUtc);
        var guid = gift.Guid.Length == 32 ? Convert.FromHexString(gift.Guid) : new byte[16];
        w.WriteBytes(guid);
        w.WriteUInt32(gift.ItemId);
        w.WriteUInt16(gift.Count);
        w.WriteDouble(received.ToOADate());
        w.WriteDouble(FloorTo45Minutes(received).ToOADate());
        w.WriteByte((byte)Math.Clamp(AccountGiftStore.DaysLeft(received, nowLocal), 0, AccountGiftStore.DaysKept));
        w.WriteByte(gift.Source);
        w.WriteZeros(3);
    }

 /// <summary><paramref name="local"/> rounded down to a multiple of 45 minutes after its midnight.</summary>
    internal static DateTime FloorTo45Minutes(DateTime local)
    {
        int minutes = local.Hour * 60 + local.Minute;
        return local.Date.AddMinutes(minutes / 45 * 45);
    }

 /// <summary>
 /// Builds RMI 2800 AnsGetAccountGift, the answer to a gift claim. Fixed 185 bytes:
 /// <c>[u32 accountId][u32 result][u64 character GID][16-byte GUID][item ID][count][source][bag record]</c>.
 /// </summary>
 /// <remarks>
 /// On success the bag record is the stack the gift landed in. On a refusal it is zeroed.
 /// </remarks>
    public static byte[] Build2800(uint accountId, uint result, ulong characterGid, ReadOnlySpan<byte> guid16,
        uint itemId, uint count, byte source, byte[]? record126)
    {
        using var w = new PacketWriter(185);
        w.WriteUInt32(accountId);
        w.WriteUInt32(result);
        w.WriteUInt64(characterGid);
        if (guid16.Length >= 16) w.WriteBytes(guid16[..16]);
        else w.WriteZeros(16);
        w.WriteUInt32(itemId);
        w.WriteUInt32(count);
        w.WriteZeros(15);
        w.WriteByte(source);
        w.WriteZeros(3);
        if (record126 is { Length: LootNotifyPackets.ItemRecordLength }) w.WriteBytes(record126);
        else w.WriteZeros(LootNotifyPackets.ItemRecordLength);
        return w.ToArray();
    }
}
