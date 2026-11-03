using RO2.GameData.Tables;

namespace RO2.Companions.Domain;

/// <summary>What a riding request (RMI 1510) does, given the ride the player is on.</summary>
public enum RideAction
{
 /// <summary>Not riding: mount the requested vehicle.</summary>
    Mount,

 /// <summary>Riding the requested vehicle: drop the ride.</summary>
    DropSame,

 /// <summary>Riding another vehicle: drop the ride and mount nothing.</summary>
    DropSwitch,
}

/// <summary>
/// Pure riding decisions: what a mount request does and which RidingInfo row or skill keeps a ride.
/// Holds no live ride registry and sends nothing.
/// </summary>
public static class RidingRules
{
 /// <summary>2719 result of a successful mount.</summary>
    public const ushort ResultMounted = 0;

 /// <summary>2719 result for a request that dismounted the player from the requested vehicle.</summary>
    public const ushort ResultDismounted = 21801;

 /// <summary>2719 result for a request naming another vehicle while riding.</summary>
    public const ushort ResultSwitchDropped = 17;

 /// <summary>
 /// The mount toggle: <paramref name="currentVehicle"/> is the vehicle the player rides, or null when
 /// not riding.
 /// </summary>
    public static RideAction Decide(uint? currentVehicle, uint requestedVehicle)
        => currentVehicle is not { } cur ? RideAction.Mount
         : cur == requestedVehicle ? RideAction.DropSame
         : RideAction.DropSwitch;

 /// <summary>The 2719 result code of <paramref name="action"/>.</summary>
    public static ushort ResultCode(RideAction action) => action switch
    {
        RideAction.Mount => ResultMounted,
        RideAction.DropSame => ResultDismounted,
        _ => ResultSwitchDropped,
    };

 /// <summary>
 /// The RidingInfo row the vehicle rides with: the LAST row of <paramref name="vehicleId"/> whose
 /// NationEnable has bit 0 set, or null when there is none.
 /// </summary>
    public static RidingInfo? RideRow(IEnumerable<RidingInfo> rows, uint vehicleId)
    {
        RidingInfo? row = null;
        foreach (var r in rows)
        {
            if (r.ID == vehicleId && (r.NationEnable & 1) != 0)
                row = r;
        }
        return row;
    }

 /// <summary>
 /// True when casting <paramref name="skillId"/> leaves the rider mounted: a riding-boost skill
 /// (RidingSkill.Skill_ID) or a vehicle's own summon skill (RidingInfo.Skill_ID).
 /// </summary>
    public static bool KeepsRide(IEnumerable<RidingSkill> boosts, IEnumerable<RidingInfo> vehicles, uint skillId)
    {
        foreach (var r in boosts)
        {
            if (r.Skill_ID == skillId) return true;
        }
        foreach (var r in vehicles)
        {
            if (r.Skill_ID == skillId) return true;
        }
        return false;
    }
}
