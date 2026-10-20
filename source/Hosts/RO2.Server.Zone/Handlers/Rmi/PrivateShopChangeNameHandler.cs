using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1479 ReqChangePrivateShopName: renames the sender's private shop.</summary>
/// <remarks>
/// <para>
/// The private-shop messages share one 61-byte struct:
/// <c>[+4 u32 owner handle][+8 u16 result][+10 u32 owner handle][+14 wchar_t[21] name, NUL-terminated][+56 u8][+57 u32]</c>.
/// The compact-length notifies (2663/2664/2665) carry it as their blob, with the length and msgTag in its first
/// 4 bytes. The raw messages (1478/2651 and 1479/2652) carry it as the whole body.
/// </para>
/// <para>
/// The request holds the new name NUL-terminated at offset 14, up to 20 characters (the 21st wchar_t is the NUL).
/// It is answered with 2652 and broadcast to nearby players as 2665.
/// </para>
/// </remarks>
public class PrivateShopChangeNameHandler : ZoneRmiHandlerBase<PrivateShopChangeNameHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqChangePrivateShopName;
    public override string Name => "PrivateShopChangeName";
    public PrivateShopChangeNameHandler(IServiceScopeFactory s, ILogger<PrivateShopChangeNameHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Size of the shared private-shop struct (the whole body of 1479 and 2652, the blob of 2665).</summary>
    private const int ShopStructBytes = 61;

    /// <summary>Offset of the shop name (wchar_t[21]) inside the struct.</summary>
    private const int NameOffset = 14;

    /// <summary>Usable name length: the 21-character buffer minus its NUL terminator.</summary>
    private const int NameMaxChars = 20;

    /// <summary>Bytes after the name buffer in the struct: the u8 at +56 and the u32 at +57.</summary>
    private const int TrailerBytes = 5;

    /// <summary>msgTag of the 2665 NfyChangePrivateShopName notification (58419).</summary>
    private const ushort MsgTagNfyChangePrivateShopName = 0xE433;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < ShopStructBytes) return;

        // The new name as raw UTF-16LE up to its NUL (or 20 characters), so it goes back out unchanged.
        var req = payload.Span;
        int nameBytes = 0;
        while (nameBytes < NameMaxChars * sizeof(char)
               && (req[NameOffset + nameBytes] != 0 || req[NameOffset + nameBytes + 1] != 0))
            nameBytes += sizeof(char);
        var name = req.Slice(NameOffset, nameBytes).ToArray();

        Logger.LogInformation("{Name} renames private shop to '{Shop}'",
            player.Name, System.Text.Encoding.Unicode.GetString(name));

        // The shop keeps the new title: the buyer-facing shop messages (2657/2658) and the end notify (2664)
        // read it from PrivateShopRegistry. A rename does not end the shop, so the viewer list is kept (SetName,
        // the shop begin, would empty it). Nothing is recorded when this zone session has no shop for the player.
        // The registry is updated before 2665 goes out, so a buyer who opens the shop after seeing 2665 reads the
        // new title.
        if (!PrivateShopRegistry.Rename(player, req.Slice(NameOffset, PrivateShopRegistry.NameBytes)))
            Logger.LogDebug("{Name} renamed a private shop this zone session has no record of", player.Name);

        // AnsChangePrivateShopName (2652): the whole 61-byte struct as a raw body, result u16 at +8 = 0 (success),
        // the name echoed at +14 and every other byte zero (including the per-shop byte at +56).
        var ans = new byte[ShopStructBytes];
        name.CopyTo(ans.AsSpan(NameOffset));
        await session.SendRmiAsync(ZoneS2COpcodes.AnsChangePrivateShopName, writer => writer.WriteBytes(ans));

        // NfyChangePrivateShopName (2665) to nearby players: a compact-length blob with msgTag 58419. The client
        // finds the shop owner by the handle at blob+10 and copies the NUL-terminated name at blob+14 into the
        // owner's shop-name string.
        var renameHandle = player.EntityId;
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfyChangePrivateShopName, writer =>
        {
            writer.WriteCompactBABody(p =>
            {
                p.WriteUInt16(MsgTagNfyChangePrivateShopName);   // blob+2  msgTag
                p.WriteUInt32(renameHandle);      // blob+4  shop owner handle (EntityId)
                p.WriteUInt16(0);                 // blob+8  result, unused in the notify
                p.WriteUInt32(renameHandle);      // blob+10 shop owner handle, the one the client reads
                p.WriteBytes(name);               // blob+14 new shop name, UTF-16LE
                // NUL terminator + rest of the name buffer, then blob+56..60 (u8 + u32), all zero.
                p.WriteZeros(PrivateShopRegistry.NameBytes - name.Length + TrailerBytes);
            });
        });
    }
}
