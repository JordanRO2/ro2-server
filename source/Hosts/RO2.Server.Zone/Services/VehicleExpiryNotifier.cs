using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.ProudNet;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Sends the 2838 OnVehicleListUpdate notice once a minute: state 8 for a timed vehicle in its last hour,
/// and state 4 for a vehicle that has just expired. Driven by <see cref="ServerTimeService"/>'s tick.
/// </summary>
/// <remarks>
/// Only vehicles the client holds are named, because naming an unknown vehicle faults the client.
/// A vehicle that expires during a warp is announced on the next tick.
/// The stored expiry is re-read on every tick, so extensions take effect without a cache.
/// </remarks>
internal sealed class VehicleExpiryNotifier
{
 /// <summary>Announcement window for state 8: 60 minutes.</summary>
    internal static readonly double ExpiringSoonWindowDays = TimeSpan.FromMinutes(60).TotalDays;

    private readonly IVehicleRepository _vehicles;
    private readonly ILogger _logger;
    private readonly VehicleExpiryWatch _watch = new();

    public VehicleExpiryNotifier(IVehicleRepository vehicles, ILogger logger)
    {
        _vehicles = vehicles;
        _logger = logger;
    }

 /// <summary>
 /// Runs one tick for a player: sends one 2838 per vehicle that is due and present in the client's list.
 /// Throws when the store or the send fails; the evaluated instant then stays unchanged, so the next tick retries.
 /// </summary>
    public async Task TickAsync(PlayerState player, ProudSession session, double nowOle, CancellationToken ct)
    {
        var since = _watch.EvaluatedSince(player);
        if (!player.MapLoaded || player.PendingGliderDismount)
            return;

        var owned = await _vehicles.GetAsync(player.CharacterId, ct);
        foreach (var record in Due(owned, since, nowOle))
        {
            
            
            if (!MayName(player.SessionId, record.RidingId))
            {
                _logger.LogDebug("{Name}: no 2838 for vehicle {Vehicle} state {State}: the last 2522 did not list it",
                    player.Name, record.RidingId, record.State);
                continue;
            }
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnVehicleListUpdate,
                VehiclePackets.BuildVehicleListUpdate2838(new[] { record }), ct);
            _logger.LogDebug("{Name}: 2838 vehicle {Vehicle} state {State} (expiry {Expiry})",
                player.Name, record.RidingId, record.State, record.ExpiryOle);
        }
        _watch.MarkEvaluated(player, nowOle);
    }

 /// <summary>
 /// Whether a 2838 may name the vehicle on the session: not when the last 2522 left it out;
 /// yes when it included it, or when no 2522 has been recorded for the session.
 /// </summary>
    internal static bool MayName(uint sessionId, uint ridingId) => VehicleClientList.Holds(sessionId, ridingId) != false;

 /// <summary>
 /// Forgets sessions that are no longer online. The version is read before the online list, so newer records are kept.
 /// </summary>
    public void Retain(IReadOnlyCollection<PlayerState> online, long clientListVersion)
    {
        _watch.Retain(online);
        VehicleClientList.Retain(online, clientListVersion);
    }

 /// <summary>
 /// The records to send at the given time, sorted by vehicle id: state 8 for a timed vehicle expiring within the window,
 /// and state 4 for a vehicle whose expiry fell since the last evaluation. Permanent vehicles are never sent.
 /// </summary>
    internal static IReadOnlyList<VehicleListRecord> Due(IReadOnlyList<OwnedVehicle> owned, double sinceOle, double nowOle)
    {
        var due = new List<VehicleListRecord>();
        foreach (var v in owned.OrderBy(v => v.RidingId))
        {
            var expiry = VehiclePackets.WholeSecondExpiry(v.ExpiryOle);
            if (expiry == 0)
                continue;
            if (expiry > nowOle)
            {
                if (expiry - nowOle <= ExpiringSoonWindowDays)
                    due.Add(new VehicleListRecord(v.RidingId, expiry, VehiclePackets.StateExpiringSoon));
            }
            else if (expiry > sinceOle)
            {
                due.Add(new VehicleListRecord(v.RidingId, expiry, VehiclePackets.StateExpired));
            }
        }
        return due;
    }
}

/// <summary>
/// Tracks the instant up to which each session's vehicle expiries have been announced, keyed by session and login time.
/// Used from a single tick loop, so it needs no locking.
/// </summary>
internal sealed class VehicleExpiryWatch
{
    private readonly Dictionary<uint, (DateTime LoginTime, double EvaluatedOle)> _bySession = new();

 /// <summary>
 /// The instant the player's expiries were last evaluated, as an OLE date on the server clock.
 /// On first sight, this is the player's login time.
 /// </summary>
    internal double EvaluatedSince(PlayerState player)
    {
        if (_bySession.TryGetValue(player.SessionId, out var entry) && entry.LoginTime == player.LoginTime)
            return entry.EvaluatedOle;
        var since = ToServerWallClockOle(player.LoginTime);
        _bySession[player.SessionId] = (player.LoginTime, since);
        return since;
    }

 /// <summary>Records that the player's expiries are announced up to the given time.</summary>
    internal void MarkEvaluated(PlayerState player, double nowOle)
        => _bySession[player.SessionId] = (player.LoginTime, nowOle);

 /// <summary>Drops the sessions that are not in the online list.</summary>
    internal void Retain(IReadOnlyCollection<PlayerState> online)
    {
        if (_bySession.Count == 0)
            return;
        var live = online.Select(p => p.SessionId).ToHashSet();
        foreach (var id in _bySession.Keys.Where(id => !live.Contains(id)).ToList())
            _bySession.Remove(id);
    }

 /// <summary>
 /// Converts a UTC instant to the server's local wall clock as an OLE date.
 /// </summary>
    internal static double ToServerWallClockOle(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToOADate();
}

/// <summary>
/// The vehicle ids each session's client holds, from the last 2522 sent on that session.
/// A 2838 may name only those ids.
/// </summary>
/// <remarks>
/// Every 2522 replaces the client's list, and a 2838 naming an unknown id can fault the client.
/// Recorded after each 2522 is sent. A session with no record is not guarded.
/// </remarks>
internal static class VehicleClientList
{
    private static readonly ConcurrentDictionary<uint, Entry> BySession = new();
    private static long _version;

    private sealed record Entry(FrozenSet<uint> Ids, long Version);

 /// <summary>Counter raised by every record; read it before listing the online players to prune by.</summary>
    internal static long Version => Interlocked.Read(ref _version);

 /// <summary>
 /// Records the vehicles of a 2522 just sent to the session, replacing the previous list.
 /// </summary>
    internal static void Record(uint sessionId, IReadOnlyList<OwnedVehicle> vehicles)
        => BySession[sessionId] = new Entry(vehicles.Select(v => v.RidingId).ToFrozenSet(), Interlocked.Increment(ref _version));

 /// <summary>
 /// Whether the last 2522 recorded for the session carried the vehicle; null when no 2522 is recorded.
 /// </summary>
    internal static bool? Holds(uint sessionId, uint ridingId)
        => BySession.TryGetValue(sessionId, out var entry) ? entry.Ids.Contains(ridingId) : null;

 /// <summary>
 /// Drops the sessions not in the online list whose record is not newer than the given version.
 /// </summary>
    internal static void Retain(IReadOnlyCollection<PlayerState> online, long recordedBy)
    {
        if (BySession.IsEmpty)
            return;
        var live = online.Select(p => p.SessionId).ToHashSet();
        foreach (var entry in BySession)
        {
            if (!live.Contains(entry.Key) && entry.Value.Version <= recordedBy)
                BySession.TryRemove(entry); 
        }
    }
}
