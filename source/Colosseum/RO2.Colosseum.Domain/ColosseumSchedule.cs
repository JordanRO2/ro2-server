using RO2.GameData;

namespace RO2.Colosseum.Domain;

/// <summary>
/// Colosseum opening hours and the start notification (2879, <c>NfyColosseumStart</c>) that announces them.
/// </summary>
/// <remarks>
/// <para>
/// The notification body is one byte: 01 when the Colosseum is open, 00 when it is closed.
/// </para>
/// <para>
/// The opening hours come from configuration (default 01:00, 13:00 and 18:00, one hour each; they are in no data table), passed in by the caller so this class stays pure. The Colosseum is open during the hours of <see cref="IsOpen"/>, based on <see cref="System.DateTimeOffset"/>. The server sends the state change about once a minute, and players who enter during opening hours are told it is open (see <see cref="AnnouncesOnEntry"/>).
/// </para>
/// </remarks>
public static class ColosseumSchedule
{
 /// <summary>Notification body byte (2879): the Colosseum is open.</summary>
    private const byte NotifyOpen = 0x01;

 /// <summary>Notification body byte (2879): the Colosseum is closed.</summary>
    private const byte NotifyClosed = 0x00;

 /// <summary>
 /// Returns true when the Colosseum is open at the given server time: inside the one-hour window of any configured open hour.
 /// </summary>
 /// <param name="serverNow">The current server time.</param>
 /// <param name="openHoursUtc">The server-clock hours at which the arena opens.</param>
    public static bool IsOpen(DateTimeOffset serverNow, IReadOnlyCollection<int> openHoursUtc)
        => openHoursUtc.Contains(serverNow.Hour);

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
 /// <param name="openHoursUtc">The server-clock hours at which the arena opens.</param>
 /// <param name="minAnnounceLevel">Lowest level that is told the Colosseum is open.</param>
    public static bool AnnouncesOnEntry(DateTimeOffset serverNow, byte level, MapData? map, IReadOnlyCollection<int> openHoursUtc, byte minAnnounceLevel)
        => IsOpen(serverNow, openHoursUtc) && level >= minAnnounceLevel && map?.Map_Type != ColosseumMapType;
}
