using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.Companions.Domain;

/// <summary>
/// Rules for the vehicles a character owns: which vehicle and duration a reins item grants, how a grant
/// or extension changes the owned list, and the whole-second normalization applied on character load.
/// Holds no storage and builds no packets.
/// </summary>
public static class VehicleRules
{
 /// <summary>ItemInfo.Item_Type of the reins items that grant a vehicle.</summary>
    public const byte ReinsItemType = 11;

 /// <summary>Record state of a permanent vehicle (1).</summary>
    public const byte StatePermanent = 1;

 /// <summary>Record state of a timed vehicle that has not yet expired (2).</summary>
    public const byte StateTimed = 2;

 /// <summary>Record state of a timed vehicle that has expired (4).</summary>
    public const byte StateExpired = 4;

 /// <summary>Record state of a 2838 for a timed vehicle expiring within the next 60 minutes (8).</summary>
    public const byte StateExpiringSoon = 8;

 /// <summary>Announcement window for state 8: 60 minutes.</summary>
    public static readonly double ExpiringSoonWindowDays = TimeSpan.FromMinutes(60).TotalDays;

 /// <summary>True when <paramref name="ridingId"/> is in the owned list and has not expired (permanent or expiry after <paramref name="nowOle"/>).</summary>
    public static bool CanRide(IReadOnlyList<OwnedVehicle> owned, uint ridingId, double nowOle)
        => owned.Any(v => v.RidingId == ridingId && StateOf(v.ExpiryOle, nowOle) != StateExpired);

 /// <summary>Record state for a vehicle with the given expiry at the given time.</summary>
    public static byte StateOf(double expiryOle, double nowOle)
        => expiryOle == 0 ? StatePermanent : expiryOle > nowOle ? StateTimed : StateExpired;

 /// <summary>
 /// The records to send at the given time, sorted by vehicle id: state 8 for a timed vehicle expiring within the window,
 /// and state 4 for a vehicle whose expiry fell since the last evaluation. Permanent vehicles are never sent.
 /// </summary>
    public static IReadOnlyList<VehicleListRecord> Due(IReadOnlyList<OwnedVehicle> owned, double sinceOle, double nowOle)
    {
        var due = new List<VehicleListRecord>();
        foreach (var v in owned.OrderBy(v => v.RidingId))
        {
            var expiry = WholeSecondExpiry(v.ExpiryOle);
            if (expiry == 0)
                continue;
            if (expiry > nowOle)
            {
                if (expiry - nowOle <= ExpiringSoonWindowDays)
                    due.Add(new VehicleListRecord(v.RidingId, expiry, StateExpiringSoon));
            }
            else if (expiry > sinceOle)
            {
                due.Add(new VehicleListRecord(v.RidingId, expiry, StateExpired));
            }
        }
        return due;
    }

 /// <summary>ItemCashInfo.Duration_Type of a permanent item.</summary>
    private const byte DurationPermanent = 0;

 /// <summary>ItemCashInfo.Duration_Type of an item that lasts Duration_Time days.</summary>
    private const byte DurationDays = 1;

 /// <summary>
 /// Resolves the vehicle a reins item grants and for how long. <paramref name="days"/> is null for a
 /// permanent grant. Returns false when the data tables do not define the grant.
 /// </summary>
 /// <remarks>
 /// Each reins item maps to a RidingDurationGroup, which maps to one vehicle; ItemCashInfo gives the
 /// duration (Duration_Type 0 is permanent, 1 is Duration_Time days).
 /// </remarks>
    public static bool TryResolveReins(GameDataStore store, uint itemId, out uint ridingId, out uint? days)
        => TryResolveReins(store.RidingDurationGroup, store.RidingInfo, store.ItemCashInfo, itemId, out ridingId, out days);

 /// <summary>Resolves a reins item against the three underlying tables.</summary>
    public static bool TryResolveReins(IReadOnlyList<RidingDurationGroup> groups, IReadOnlyList<RidingInfo> vehicles,
        IReadOnlyList<ItemCashInfo> cashInfo, uint itemId, out uint ridingId, out uint? days)
    {
        ridingId = 0;
        days = null;
        var group = groups.FirstOrDefault(g => g.Item_ID == itemId);
        if (group is null) return false;
        var vehicle = vehicles.FirstOrDefault(r => r.RidingDuration_GroupID == group.RidingDuration_ID);
        if (vehicle is null) return false;
        var cash = cashInfo.FirstOrDefault(c => c.Item_ID == itemId);
        if (cash is null) return false;
        switch (cash.Duration_Type)
        {
            case DurationPermanent:
                break;
            case DurationDays when cash.Duration_Time > 0:
                days = cash.Duration_Time;
                break;
            default:
                return false;
        }
        ridingId = vehicle.ID;
        return true;
    }

 /// <summary>
 /// Resolves the days an extension item adds to a vehicle: the item's DurationExtendGroup bit must be
 /// accepted by one of the vehicle's reins items (accepted bits are OR-ed across them), and the item must
 /// last <c>Duration_Time &gt; 0</c> days (<c>Duration_Type</c> 1 only). Returns false otherwise.
 /// </summary>
    public static bool ResolveExtension(IReadOnlyList<DurationExtendGroup> extendGroups, IReadOnlyList<ItemCashInfo> cashInfo,
        IReadOnlyList<RidingInfo> vehicles, IReadOnlyList<RidingDurationGroup> reinsGroups, uint ridingId, uint itemId, out uint days)
    {
        days = 0;
        var bit = extendGroups.FirstOrDefault(g => g.Item_ID == itemId)?.DurationExtend_ID ?? 0;
        if (bit == 0) return false;
        var cash = cashInfo.FirstOrDefault(c => c.Item_ID == itemId);
        if (cash is not { Duration_Type: DurationDays, Duration_Time: > 0 }) return false;
        var vehicle = vehicles.FirstOrDefault(r => r.ID == ridingId);
        if (vehicle is null) return false;
        var reins = reinsGroups.Where(g => g.RidingDuration_ID == vehicle.RidingDuration_GroupID)
            .Select(g => g.Item_ID).ToHashSet();
        int accepted = 0;
        foreach (var c in cashInfo)
            if (reins.Contains(c.Item_ID)) accepted |= c.DurationExtend_GroupID;
        if ((accepted & bit) == 0) return false;
        days = cash.Duration_Time;
        return true;
    }

 /// <summary>
 /// Cuts <paramref name="expiryOle"/> to its whole second. A value already on a whole second is returned
 /// unchanged, and 0 (permanent) stays 0.
 /// </summary>
 /// <remarks>
 /// The value is first read to the millisecond, as <see cref="DateTime.FromOADate"/> does, so a whole
 /// second held a hair below is not cut to the second before it.
 /// </remarks>
    public static double WholeSecondExpiry(double expiryOle)
    {
        if (expiryOle == 0)
            return 0;
        var at = DateTime.FromOADate(expiryOle);
        return at.AddTicks(-(at.Ticks % TimeSpan.TicksPerSecond)).ToOADate();
    }

 /// <summary>
 /// Returns <paramref name="owned"/> with every expiry cut to its whole second. When nothing changes, the
 /// same list instance is returned.
 /// </summary>
    public static IReadOnlyList<OwnedVehicle> CutToWholeSeconds(IReadOnlyList<OwnedVehicle> owned)
    {
        List<OwnedVehicle>? result = null;
        for (int i = 0; i < owned.Count; i++)
        {
            var v = owned[i];
            var cut = WholeSecondExpiry(v.ExpiryOle);
            if (result is null)
            {
                if (BitConverter.DoubleToInt64Bits(cut) == BitConverter.DoubleToInt64Bits(v.ExpiryOle))
                    continue;
                result = new List<OwnedVehicle>(owned.Count);
                for (int j = 0; j < i; j++)
                    result.Add(new OwnedVehicle { RidingId = owned[j].RidingId, ExpiryOle = owned[j].ExpiryOle });
            }
            result.Add(new OwnedVehicle { RidingId = v.RidingId, ExpiryOle = cut });
        }
        return result is null ? owned : result;
    }

 /// <summary>
 /// Returns <paramref name="owned"/> after a reins item grants <paramref name="ridingId"/> for
 /// <paramref name="days"/> (null means permanent) at <paramref name="nowOle"/>, sorted by RidingInfo ID.
 /// </summary>
 /// <remarks>
 /// A grant to a vehicle that is not owned, or is expired, sets the expiry to now plus the days. A grant
 /// to a vehicle still in use adds the days to its current expiry. The new expiry is rounded to float32.
 /// A permanent grant makes the vehicle permanent, and a timed grant leaves a permanent vehicle permanent.
 /// </remarks>
    public static IReadOnlyList<OwnedVehicle> Grant(IReadOnlyList<OwnedVehicle> owned, uint ridingId, uint? days, double nowOle)
    {
        var current = owned.FirstOrDefault(v => v.RidingId == ridingId);
        double expiry;
        if (days is null || current is { ExpiryOle: 0 })
            expiry = 0;
        else
        {
            var from = current is not null && current.ExpiryOle > nowOle ? current.ExpiryOle : nowOle;
            expiry = (float)(from + days.Value);
        }

        var result = owned.Where(v => v.RidingId != ridingId)
            .Select(v => new OwnedVehicle { RidingId = v.RidingId, ExpiryOle = v.ExpiryOle })
            .ToList();
        result.Add(new OwnedVehicle { RidingId = ridingId, ExpiryOle = expiry });
        result.Sort((a, b) => a.RidingId.CompareTo(b.RidingId));
        return result;
    }

 /// <summary>
 /// Returns <paramref name="owned"/> after a reins item extends the timed vehicle <paramref name="ridingId"/>
 /// by <paramref name="days"/> at <paramref name="nowOle"/>, or null when the vehicle is not owned or is
 /// permanent (nothing to extend).
 /// </summary>
 /// <remarks>An expired vehicle counts from now, as <see cref="Grant"/> does.</remarks>
    public static IReadOnlyList<OwnedVehicle>? TryExtendTimed(IReadOnlyList<OwnedVehicle> owned, uint ridingId, uint days, double nowOle)
        => owned.FirstOrDefault(v => v.RidingId == ridingId) is { ExpiryOle: not 0 }
            ? Grant(owned, ridingId, days, nowOle)
            : null;
}
