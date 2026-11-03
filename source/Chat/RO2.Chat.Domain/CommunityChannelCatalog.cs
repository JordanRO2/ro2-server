using System.Globalization;

namespace RO2.Chats.Domain;

/// <summary>
/// The always-on community chat channels that every player joins automatically on the Chat server.
/// </summary>
/// <remarks>
/// After login, each channel is announced to the client with RMI 23534, carrying its index and a fixed 16-byte
/// GUID. The client echoes that GUID in room chat (23018), and the server echoes it in the broadcast (23528).
/// The GUIDs only need to be stable and unique per channel. The channel indices are 12 (Public), 13 (Trade) and
/// 14 (Party).
/// </remarks>
public static class CommunityChannelCatalog
{
    // Channel indices as the client knows them.
    private const ushort PublicIndex = 12;
    private const ushort TradeIndex = 13;
    private const ushort PartyIndex = 14;

    /// <summary>First byte of every channel GUID, a fixed marker (0xC0).</summary>
    private const byte GuidMarkerByte = 0xC0;

    /// <summary>Number of bytes in the ascending run that follows <see cref="GuidMarkerByte"/> in the GUID.</summary>
    private const int GuidRunLength = 14;

    /// <summary>
    /// Step of the ascending run: run byte N (1-based) is <c>N * 0x11</c>, the repeated-nibble values
    /// 11 22 33 44 55 66 77 88 99 AA BB CC DD EE.
    /// </summary>
    private const byte GuidRunStep = 0x11;

    /// <summary>
    /// First 15 of the 16 GUID bytes, shared by every channel: <see cref="GuidMarkerByte"/> followed by the
    /// <see cref="GuidRunLength"/>-byte ascending run (bytes 1..14, 11 22 ... EE). The GUID is an opaque handle, not a
    /// set of fields. The 16th byte is the channel index (12/13/14), which makes each GUID unique. The client only
    /// echoes the whole value back, so it needs to be stable per channel and nothing more.
    /// </summary>
    private static readonly byte[] GuidPrefix = BuildGuidPrefix();

    private static byte[] BuildGuidPrefix()
    {
        var prefix = new byte[1 + GuidRunLength];
        prefix[0] = GuidMarkerByte;
        for (var n = 1; n <= GuidRunLength; n++)
            prefix[n] = (byte)(n * GuidRunStep);
        return prefix;
    }

    // String table ids the client localizes as the tab labels "Public", "Trade" and "Party".
    private const ushort PublicNameStringId = 11802;
    private const ushort TradeNameStringId = 11803;
    private const ushort PartyNameStringId = 11804;

    // Default tab colors (ARGB) of a player who never recolored a tab.
    private const uint PublicDefaultColor = 0xFFF7941Du;
    private const uint TradeDefaultColor = 0xFFF0AA46u;
    private const uint PartyDefaultColor = 0xFF8CAAFAu;

    private static byte[] MakeGuid(ushort channelIndex)
    {
        var guid = new byte[GuidPrefix.Length + 1];
        GuidPrefix.CopyTo(guid, 0);
        guid[^1] = (byte)channelIndex;
        return guid;
    }

    /// <summary>
    /// A community channel. <c>Name</c> is used only in log output. <c>DefaultColor</c> is the tab color, as an ARGB
    /// value, for a player who has not chosen a color. A color chosen by the player is stored and replaces it.
    /// <c>NameStringId</c> is the string table id that the client uses to localize the tab label.
    /// </summary>
    public sealed record Channel(ushort Index, string Name, byte[] Guid, ushort NameStringId, uint DefaultColor)
    {
        public string WireName => " " + NameStringId.ToString(CultureInfo.InvariantCulture);
    }

    public static readonly Channel Public = new(PublicIndex, "Public", MakeGuid(PublicIndex),
        NameStringId: PublicNameStringId, DefaultColor: PublicDefaultColor);

    public static readonly Channel Trade = new(TradeIndex, "Trade", MakeGuid(TradeIndex),
        NameStringId: TradeNameStringId, DefaultColor: TradeDefaultColor);

    public static readonly Channel LookingForParty = new(PartyIndex, "Party", MakeGuid(PartyIndex),
        NameStringId: PartyNameStringId, DefaultColor: PartyDefaultColor);

    public static readonly Channel[] All = { Public, Trade, LookingForParty };

    /// <summary>Returns the channel with the given 16-byte GUID, or null when there is none.</summary>
    public static Channel? ByGuid(ReadOnlySpan<byte> guid)
    {
        foreach (var c in All)
            if (guid.SequenceEqual(c.Guid)) return c;
        return null;
    }
}
