using RO2.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Colosseum opening hours and the start notification (2879, <c>NfyColosseumStart</c>) that announces them.
/// </summary>
/// <remarks>
/// <para>
/// The notification body is one byte: 01 when the Colosseum is open, 00 when it is closed.
/// </para>
/// <para>
/// The opening hours are fixed in this class (01:00, 13:00 and 18:00, one hour each; they are in no data table). The Colosseum is open during the hours of <see cref="IsOpen"/>, based on <see cref="ServerWallClock"/>. The server sends the state change about once a minute, and players who enter during opening hours are told it is open (see <see cref="AnnouncesOnEntry"/>).
/// </para>
/// </remarks>
public static class ColosseumSchedule
{
 /// <summary>Server wall-clock hour at which the first daily window opens: [01:00, 02:00).</summary>
    private const int OpenHourNight = 1;

 /// <summary>Server wall-clock hour at which the second daily window opens: [13:00, 14:00).</summary>
    private const int OpenHourAfternoon = 13;

 /// <summary>Server wall-clock hour at which the third daily window opens: [18:00, 19:00).</summary>
    private const int OpenHourEvening = 18;

 /// <summary>Notification body byte (2879): the Colosseum is open.</summary>
    private const byte NotifyOpen = 0x01;

 /// <summary>Notification body byte (2879): the Colosseum is closed.</summary>
    private const byte NotifyClosed = 0x00;

 /// <summary>
 /// Lowest level that is told on world entry that the Colosseum is open. Level 1 characters are never told; the
 /// real minimum is not in any data table, so only level 1 is excluded.
 /// </summary>
    private const byte MinEntryAnnounceLevel = 2;

 /// <summary>
 /// Returns true when the Colosseum is open at the given server time: inside [01:00, 02:00), [13:00, 14:00) or [18:00, 19:00).
 /// </summary>
 /// <param name="serverNow">The current server time.</param>
    public static bool IsOpen(DateTimeOffset serverNow)
        => serverNow.Hour is OpenHourNight or OpenHourAfternoon or OpenHourEvening;

 /// <summary>Builds the start notification body: <c>[u8 open]</c>, 01 when open, 00 when closed.</summary>
    public static byte[] Build2879(bool open) => new[] { open ? NotifyOpen : NotifyClosed };

 /// <summary>Map_Type of the Colosseum arena (the two Colosseum maps in the map list, map 4109, are the only rows of type 3).</summary>
    private const byte ColosseumMapType = 3;

 /// <summary>
 /// Returns whether a character entering the world should be told that the Colosseum is open.
 /// </summary>
 /// <remarks>
 /// Entries are announced only during opening hours, and only to characters above the minimum level, outside the arena itself.
 /// </remarks>
 /// <param name="serverNow">The current server time.</param>
 /// <param name="level">The entering character's level.</param>
 /// <param name="map">The map being entered, if known.</param>
    public static bool AnnouncesOnEntry(DateTimeOffset serverNow, byte level, MapData? map)
        => IsOpen(serverNow) && level >= MinEntryAnnounceLevel && map?.Map_Type != ColosseumMapType;
}
