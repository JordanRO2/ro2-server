using System.Runtime.CompilerServices;
using RO2.Companions.Domain;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Per-player glider identity the movement handlers share: the ride 1313 TakeGlider opens (and 1314 GetOffGlider
/// closes) and the flight token 1515 TakeFarGlider issues (and 1528 GliderTeleport consumes).
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the player's <see cref="PlayerState"/> through a <see cref="ConditionalWeakTable{TKey,TValue}"/>, so an
/// entry lives exactly as long as the session's player object and a logout or zone restart leaves nothing behind
/// (the <see cref="SkillMoveTracker"/> pattern). Runtime-only, no DI registration.
/// </para>
/// <para>
/// The store carries IDENTITY only (which route, which station, until when), never money: the fare is taken by the
/// handler that consumes the record, from the wallet (<c>TryDebit</c>), not from anything held here.
/// </para>
/// </remarks>
public static class GliderRideStore
{
    /// <summary>The 1515 flight-token window; the value lives with the rule in <see cref="GliderRideState"/>.</summary>
    public static readonly TimeSpan FlightTokenLifetime = GliderRideState.FlightTokenLifetime;

    private static readonly ConditionalWeakTable<PlayerState, GliderRideState> Entries = new();

    // ---- Ride record: 1313 TakeGlider -> 1314 GetOffGlider ---------------------------------------------------

    /// <summary>Records that <paramref name="player"/> mounted a glider on <paramref name="route"/> (the spline.json
    /// index of a ride row), replacing any earlier record.</summary>
    public static void PutRide(PlayerState player, ushort route)
    {
        var e = Entries.GetValue(player, _ => new GliderRideState());
        lock (e)
            e.PutRide(route);
    }

    /// <summary>Whether <paramref name="player"/> holds a ride record: the "already mounted" signal 1313 checks.</summary>
    public static bool HasRide(PlayerState player)
    {
        if (!Entries.TryGetValue(player, out var e)) return false;
        lock (e)
            return e.HasRide;
    }

    /// <summary>
    /// Takes the ride record: true with its route and the record removed, false when there is none. Single
    /// consume, so a repeated 1314 finds nothing and cannot charge the fare twice.
    /// </summary>
    public static bool TryTakeRide(PlayerState player, out ushort route)
    {
        route = 0;
        if (!Entries.TryGetValue(player, out var e)) return false;
        lock (e)
            return e.TryTakeRide(out route);
    }

    // ---- Flight token: 1515 TakeFarGlider -> 1528 GliderTeleport ---------------------------------------------

    /// <summary>Issues the flight token for <paramref name="station"/>, valid until <paramref name="expiryUtc"/>,
    /// replacing any earlier one (a newer 1515 supersedes the older flight).</summary>
    public static void IssueFlightToken(PlayerState player, ushort station, DateTime expiryUtc)
    {
        var e = Entries.GetValue(player, _ => new GliderRideState());
        lock (e)
            e.IssueFlightToken(station, expiryUtc);
    }

    /// <summary>
    /// Consumes the flight token: true only when one exists, <paramref name="station"/> matches it and
    /// <paramref name="now"/> is not past its expiry; the token is then nulled (single use). An expired token is
    /// dropped and refused. A token for a different station is refused and kept, so a forged 1528 naming another
    /// station cannot burn the legitimate flight.
    /// </summary>
    public static bool TryConsumeFlightToken(PlayerState player, ushort station, DateTime now)
    {
        if (!Entries.TryGetValue(player, out var e)) return false;
        lock (e)
            return e.TryConsumeFlightToken(station, now);
    }
}
