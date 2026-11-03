using System.Text;
using RO2.Chats.Domain;
using RO2.Network.Packets;

namespace RO2.Server.Chat;

/// <summary>
/// The always-on community chat channels that every player joins automatically on the Chat server.
/// </summary>
/// <remarks>
/// After login, each channel is announced to the client with RMI 23534, carrying its index and a fixed 16-byte
/// GUID. The client echoes that GUID in room chat (23018), and the server echoes it in the broadcast (23528).
/// The GUIDs only need to be stable and unique per channel. The channel indices are 12 (Public), 13 (Trade) and
/// 14 (Party).
/// </remarks>
public static class CommunityChannels
{
    // The pure catalog (indices, GUIDs, string ids, default colors) lives in RO2.Chats.Domain.
    public static CommunityChannelCatalog.Channel Public => CommunityChannelCatalog.Public;
    public static CommunityChannelCatalog.Channel Trade => CommunityChannelCatalog.Trade;
    public static CommunityChannelCatalog.Channel LookingForParty => CommunityChannelCatalog.LookingForParty;
    public static CommunityChannelCatalog.Channel[] All => CommunityChannelCatalog.All;
    public static CommunityChannelCatalog.Channel? ByGuid(ReadOnlySpan<byte> guid) => CommunityChannelCatalog.ByGuid(guid);

    // Channel-list (RMI 23513) record fields.
    private const byte ChannelTypeCommunity = 1;    // the "type" byte of every channel record
    private const ushort ReservedHeader = 0;        // leading u16 of the 23513 and 23505 bodies

 /// <summary>
 /// Builds the body of RMI 23513, the channel list that populates the client's default tabs. Each channel is written
 /// with its GUID, color, index, type and localized name. Colors are taken from <paramref name="colors"/> by channel
 /// index, and a channel without a stored color uses its default.
 /// Body layout: [reserved:u16][innerLen:u32][count:u16], then per channel
 /// [guid:16][color:u32][index:u8][type:u8][nameLen:u8][name:UTF-16LE]. The name is the string-id form
 /// (<see cref="CommunityChannelCatalog.Channel.WireName"/>), not the display name, because the client localizes the label itself.
 /// </summary>
    public static byte[] BuildChannelListBody(IReadOnlyDictionary<ushort, uint> colors)
    {
        using var w = new PacketWriter();
        w.WriteUInt16(ReservedHeader);
        var lenPos = w.Reserve(4);      // inner length, filled in below
        int start = w.Length;
        w.WriteUInt16((ushort)All.Length);
        foreach (var ch in All)
        {
            w.WriteBytes(ch.Guid);
            // The color the player chose, otherwise the channel default.
            w.WriteUInt32(colors.TryGetValue(ch.Index, out var argb) ? argb : ch.DefaultColor);
            w.WriteByte((byte)ch.Index);
            w.WriteByte(ChannelTypeCommunity);
            // The string-id form rather than the display name.
            var nameW = Encoding.Unicode.GetBytes(ch.WireName);
            w.WriteByte((byte)(nameW.Length / 2));
            w.WriteBytes(nameW);
        }
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.GetWrittenSpan().ToArray();
    }
}
