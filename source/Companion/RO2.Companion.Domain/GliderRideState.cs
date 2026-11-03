namespace RO2.Companions.Domain;

/// <summary>
/// One player's glider identity: the ride 1313 TakeGlider opens (1314 GetOffGlider closes) and the flight token
/// 1515 TakeFarGlider issues (1528 GliderTeleport consumes). Pure state machine: time comes in as an argument, and
/// the host owns the per-player holder and the locking.
/// </summary>
public sealed class GliderRideState
{
    /// <summary>
    /// How long a 1515 flight token stays valid: every 1528 that is not a duplicate follows a 1515 within 17 s
    /// (GliderTeleportHandler). That is the observed maximum, so it is also the bound; the token is used once, so
    /// this is the replay window, not a rate.
    /// </summary>
    public static readonly TimeSpan FlightTokenLifetime = TimeSpan.FromSeconds(17);

    private ushort? _route;
    private ushort? _tokenStation;
    private DateTime _tokenExpiryUtc;

    /// <summary>Records a ride on <paramref name="route"/> (the spline.json index), replacing any earlier record.</summary>
    public void PutRide(ushort route) => _route = route;

    /// <summary>Whether a ride record is held: the "already mounted" signal 1313 checks.</summary>
    public bool HasRide => _route.HasValue;

    /// <summary>
    /// Takes the ride record: true with its route and the record removed, false when there is none. Single
    /// consume, so a repeated 1314 finds nothing and cannot charge the fare twice.
    /// </summary>
    public bool TryTakeRide(out ushort route)
    {
        route = 0;
        if (_route is not { } r) return false;
        route = r;
        _route = null;
        return true;
    }

    /// <summary>Issues the flight token for <paramref name="station"/>, valid until <paramref name="expiryUtc"/>,
    /// replacing any earlier one (a newer 1515 supersedes the older flight).</summary>
    public void IssueFlightToken(ushort station, DateTime expiryUtc)
    {
        _tokenStation = station;
        _tokenExpiryUtc = expiryUtc;
    }

    /// <summary>
    /// Consumes the flight token: true only when one exists, <paramref name="station"/> matches it and
    /// <paramref name="nowUtc"/> is not past its expiry; the token is then nulled (single use). An expired token is
    /// dropped and refused. A token for a different station is refused and kept, so a forged 1528 naming another
    /// station cannot burn the legitimate flight.
    /// </summary>
    public bool TryConsumeFlightToken(ushort station, DateTime nowUtc)
    {
        if (_tokenStation is not { } held) return false;
        if (nowUtc > _tokenExpiryUtc)
        {
            _tokenStation = null;
            return false;
        }
        if (held != station) return false;
        _tokenStation = null;
        return true;
    }
}
