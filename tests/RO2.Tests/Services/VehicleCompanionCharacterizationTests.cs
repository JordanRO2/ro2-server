using System.Text.Json;
using RO2.Companions.Domain;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// B11 S1: characterization of the pure Companion rules a later carve moves (<see cref="OwnedVehicle"/>, <see cref="VehicleRules"/>,
/// the 2522/2838 state codes, the 1510 mount-toggle codes, <see cref="MountService"/>, <see cref="GliderRideStore"/>). Every value
/// below was observed from the current code, not derived, and every clock is passed in explicitly. The live 1510 mount/dismount/switch
/// frames (RidingServiceTests), the 2838 tick (VehicleListUpdate2838Tests), the 2522 cut (LoginEntryAndVehicleReauditTests), the reins
/// resolution against the real tables (ItemBindHistoryTests) and the glider ride/token basics (GliderRideStoreTests) are already pinned
/// elsewhere; this adds the boundaries they do not reach.
/// </summary>
[Collection(MountStateCollection.Name)]
public class VehicleCompanionCharacterizationTests
{
    private const double Now = 46223.5720;

    private static string Bits(double d) => Convert.ToHexStringLower(BitConverter.GetBytes(d));

    private static OwnedVehicle V(uint id, double expiry) => new() { RidingId = id, ExpiryOle = expiry };

    // ---------------------------------------------------------------------------------------------- Grant

    [DataTheory]
    // current (null = not owned), days (-1 = permanent), expected expiry bits
    [InlineData(null, 5, "000000409292e640")]               // now + 5, rounded to float32 (46228.5703125, not 46228.572)
    [InlineData(null, 1, "000000401292e640")]
    [InlineData(null, 30, "00000040b295e640")]
    [InlineData(46223.582, 7, "000000a0d292e640")]          // still in use (current > now): counts from the CURRENT expiry
    [InlineData(46223.562, 7, "00000040d292e640")]          // expired: counts from now
    [InlineData(Now, 7, "00000040d292e640")]                // current == now is not "in use": from now (same bits as expired)
    [InlineData(46223.57421875, 3, "000000605292e640")]
    [InlineData(null, -1, "0000000000000000")]              // permanent grant on a new vehicle
    [InlineData(46223.582, -1, "0000000000000000")]         // permanent grant on a timed one makes it permanent
    [InlineData(0.0, 5, "0000000000000000")]                // a timed grant leaves a permanent vehicle permanent
    public void Grant_expiry_is_float32_of_from_plus_days(double? current, int days, string expectedBits)
    {
        var owned = current is null ? new List<OwnedVehicle>() : new List<OwnedVehicle> { V(1, current.Value) };

        var result = VehicleRules.Grant(owned, 1, days < 0 ? null : (uint)days, Now);

        Assert.Equal(expectedBits, Bits(Assert.Single(result).ExpiryOle));
        if (days >= 0 && current != 0.0)
        {
            var from = current is { } c && c > Now ? c : Now;
            Assert.Equal((double)(float)(from + days), result[0].ExpiryOle);          // the float32 cast is the rounding
        }
    }

    [DataFact]
    public void Grant_returns_a_new_sorted_list_of_new_instances_and_never_touches_the_input()
    {
        var a = V(300, 46300.5);
        var b = V(100, 0);
        var owned = new List<OwnedVehicle> { a, b };

        var result = VehicleRules.Grant(owned, 200, 7, Now);

        Assert.Equal(new[] { 100u, 200u, 300u }, result.Select(v => v.RidingId));      // sorted by RidingInfo.ID
        Assert.Equal(new[] { 300u, 100u }, owned.Select(v => v.RidingId));              // the input keeps its order and size
        Assert.Equal(new[] { 46300.5, 0d }, owned.Select(v => v.ExpiryOle));
        Assert.DoesNotContain(result, v => ReferenceEquals(v, a) || ReferenceEquals(v, b));   // copies, not the stored objects
        Assert.Equal(46300.5, result[2].ExpiryOle);                                      // the others are carried unchanged (no float32 pass)

        // Granting a vehicle that is owned REPLACES it (one row per vehicle).
        var again = VehicleRules.Grant(result, 200, 7, Now);
        Assert.Equal(3, again.Count);
        Assert.Equal(new[] { 100u, 200u, 300u }, again.Select(v => v.RidingId));
    }

    [DataFact]
    public void TryExtendTimed_is_null_for_not_owned_or_permanent_and_otherwise_a_Grant()
    {
        var owned = new List<OwnedVehicle> { V(100, 0), V(200, Now + 2), V(300, Now - 5) };

        Assert.Null(VehicleRules.TryExtendTimed(owned, 999, 5, Now));   // not owned
        Assert.Null(VehicleRules.TryExtendTimed(owned, 100, 5, Now));   // permanent
        Assert.Equal(new[] { 100u, 200u, 300u }, owned.Select(v => v.RidingId));

        var live = VehicleRules.TryExtendTimed(owned, 200, 5, Now)!;     // counts from the current expiry, float32
        Assert.Equal(Bits((float)(Now + 2 + 5)), Bits(live.Single(v => v.RidingId == 200).ExpiryOle));

        var expired = VehicleRules.TryExtendTimed(owned, 300, 3, Now)!;  // an expired vehicle counts from now
        Assert.Equal(Bits((float)(Now + 3)), Bits(expired.Single(v => v.RidingId == 300).ExpiryOle));
    }

    // ---------------------------------------------------------------------------------------------- whole-second cut

    [DataFact]
    public void CutToWholeSeconds_returns_the_SAME_instance_when_nothing_changes()
    {
        var whole = new DateTime(2026, 7, 21, 8, 20, 37).ToOADate();

        var empty = new List<OwnedVehicle>();
        Assert.Same(empty, VehicleRules.CutToWholeSeconds(empty));

        var unchanged = new List<OwnedVehicle> { V(90140000, 0), V(90190000, whole), V(90220000, whole) };
        var cut = VehicleRules.CutToWholeSeconds(unchanged);
        Assert.Same(unchanged, cut);                                                     // repos rely on ReferenceEquals to skip the write
    }

    [DataFact]
    public void CutToWholeSeconds_builds_a_new_list_of_new_instances_in_the_same_order_when_one_expiry_changes()
    {
        var whole = new DateTime(2026, 7, 21, 8, 20, 37).ToOADate();
        var fractional = new DateTime(2026, 7, 21, 8, 20, 37, 500).ToOADate();
        var first = V(300, whole);
        var input = new List<OwnedVehicle> { first, V(100, fractional), V(200, 0) };    // unsorted: the cut does not sort

        var cut = VehicleRules.CutToWholeSeconds(input);

        Assert.NotSame(input, cut);
        Assert.Equal(new[] { 300u, 100u, 200u }, cut.Select(v => v.RidingId));
        Assert.Equal(new[] { whole, whole, 0d }, cut.Select(v => v.ExpiryOle));
        Assert.All(cut, v => Assert.DoesNotContain(v, input));                           // every element is a fresh instance
        Assert.Equal(fractional, input[1].ExpiryOle);                                    // the input is not mutated
        Assert.Equal(whole, first.ExpiryOle);
    }

    [DataFact]
    public void CutToWholeSeconds_compares_bit_patterns_so_a_negative_zero_is_a_change()
    {
        // WholeSecondExpiry(-0.0) is +0.0 (== 0 returns the literal 0); the bit pattern differs, so the list is rebuilt.
        var input = new List<OwnedVehicle> { V(1, -0.0) };

        var cut = VehicleRules.CutToWholeSeconds(input);

        Assert.NotSame(input, cut);
        Assert.Equal("0000000000000000", Bits(cut[0].ExpiryOle));
        Assert.Equal("0000000000000080", Bits(input[0].ExpiryOle));
    }

    [DataTheory]
    // offset from a whole second in ms, expected whole-second delta: the value is first read to the millisecond (FromOADate rounds).
    [InlineData(-0.6, -1)]      // rounds to 36.999 -> cut to :36
    [InlineData(-0.4, 0)]       // rounds to 37.000 -> a whole second held a hair below is NOT cut to the second before
    [InlineData(0.0, 0)]
    [InlineData(0.4, 0)]
    [InlineData(0.6, 0)]
    [InlineData(999.4, 0)]      // 37.999 -> :37
    [InlineData(999.6, 1)]      // rounds to 38.000 -> :38
    [InlineData(1000.4, 1)]
    public void WholeSecondExpiry_reads_to_the_millisecond_before_cutting(double offsetMs, int deltaSeconds)
    {
        var whole = new DateTime(2026, 7, 21, 8, 20, 37).ToOADate();

        var cut = VehicleRules.WholeSecondExpiry(whole + offsetMs / 86_400_000.0);

        Assert.Equal(Bits(new DateTime(2026, 7, 21, 8, 20, 37).AddSeconds(deltaSeconds).ToOADate()), Bits(cut));
    }

    [DataFact]
    public void WholeSecondExpiry_keeps_zero_and_a_whole_second_one_ulp_below_is_restored()
    {
        Assert.Equal(0d, VehicleRules.WholeSecondExpiry(0));
        var whole = new DateTime(2026, 7, 21, 8, 20, 37).ToOADate();
        Assert.Equal(Bits(whole), Bits(VehicleRules.WholeSecondExpiry(Math.BitDecrement(whole))));
        Assert.Equal(Bits(whole), Bits(VehicleRules.WholeSecondExpiry(whole)));
        Assert.Equal(VehicleRules.WholeSecondExpiry(whole + 0.3 / 86400), VehiclePackets.WholeSecondExpiry(whole + 0.3 / 86400));   // the packets alias
    }

    // ---------------------------------------------------------------------------------------------- state codes + 2838 selection

    [DataFact]
    public void The_record_constants_and_state_codes_are_pinned()
    {
        Assert.Equal(15, VehiclePackets.RecordLength);
        Assert.Equal(2, VehiclePackets.CountFieldLength);
        Assert.Equal(4, VehiclePackets.EnvelopeLengthFieldLength);
        Assert.Equal((byte)1, VehiclePackets.RecordMarker);
        Assert.Equal((byte)0, VehiclePackets.RecordTail);
        Assert.Equal((byte)1, VehicleRules.StatePermanent);
        Assert.Equal((byte)2, VehicleRules.StateTimed);
        Assert.Equal((byte)4, VehicleRules.StateExpired);
        Assert.Equal((byte)8, VehicleRules.StateExpiringSoon);
        Assert.Equal((byte)11, VehicleRules.ReinsItemType);
    }

    [DataTheory]
    [InlineData(0.0, 1)]                  // permanent
    [InlineData(46223.5000001, 2)]        // a hair after now: timed
    [InlineData(46223.5, 4)]              // exactly now: expired (strictly greater is needed to be timed)
    [InlineData(46223.4999999, 4)]
    [InlineData(100.0, 4)]
    [InlineData(-5.0, 4)]                 // any non-zero value not after now, negative included
    [InlineData(99999.0, 2)]
    public void StateOf_is_permanent_timed_or_expired_and_never_expiring_soon(double expiry, byte state)
    {
        Assert.Equal(state, VehicleRules.StateOf(expiry, 46223.5));
    }

    [DataFact]
    public void ExpiringSoonWindow_is_sixty_minutes_and_the_tick_boundary_is_pinned()
    {
        Assert.Equal(TimeSpan.FromMinutes(60).TotalDays, VehicleRules.ExpiringSoonWindowDays);
        Assert.Equal("555555555555a53f", Bits(VehicleRules.ExpiringSoonWindowDays));   // 0.041666666666666664
    }

    [DataTheory]
    // seconds after "now" at which the vehicle expires -> the state of the single due record, 0 = nothing due
    [InlineData(1, 8)]
    [InlineData(3599, 8)]
    [InlineData(3600, 0)]                 // exactly an hour: excluded by the double subtraction (diff 0.04166666667 > the window), not by "<"
    [InlineData(3601, 0)]
    public void Due_expiring_soon_window_edge(int secondsAfterNow, byte state)
    {
        var now = new DateTime(2026, 9, 26, 7, 0, 0).ToOADate();
        var expiry = new DateTime(2026, 9, 26, 7, 0, 0).AddSeconds(secondsAfterNow).ToOADate();

        var due = VehicleRules.Due(new[] { V(1, expiry) }, now - 1, now);

        if (state == 0) Assert.Empty(due);
        else
        {
            var r = Assert.Single(due);
            Assert.Equal(state, r.State);
            Assert.Equal(expiry, r.ExpiryOle);
        }
    }

    [DataFact]
    public void Due_expired_edge_is_strict_on_since_and_inclusive_of_now_on_the_whole_second()
    {
        var since = new DateTime(2026, 9, 26, 6, 59, 0).ToOADate();
        var now = new DateTime(2026, 9, 26, 7, 0, 0).ToOADate();

        // expiry == since: already announced, never again.
        Assert.Empty(VehicleRules.Due(new[] { V(1, since) }, since, now));
        // expiry == now: not "> now", so it is the expired branch (state 4), announced because it is after since.
        Assert.Equal(VehicleRules.StateExpired, Assert.Single(VehicleRules.Due(new[] { V(1, now) }, since, now)).State);
        // one ulp after since still cuts back to since (the whole-second cut runs before the compare): nothing due.
        Assert.Empty(VehicleRules.Due(new[] { V(1, Math.BitIncrement(since)) }, since, now));
        // one whole second after since: announced as expired.
        var oneSecondAfter = new DateTime(2026, 9, 26, 6, 59, 1).ToOADate();
        Assert.Equal(VehicleRules.StateExpired, Assert.Single(VehicleRules.Due(new[] { V(1, oneSecondAfter) }, since, now)).State);

        // The expiry is cut to its whole second first: 07:00:00.900 is treated as 07:00:00 == now, so it is EXPIRED (not soon),
        // and the record carries the cut value.
        var fractional = new DateTime(2026, 9, 26, 7, 0, 0, 900).ToOADate();
        var r = Assert.Single(VehicleRules.Due(new[] { V(1, fractional) }, since, now));
        Assert.Equal(VehicleRules.StateExpired, r.State);
        Assert.Equal(Bits(now), Bits(r.ExpiryOle));
    }

    [DataFact]
    public void Due_skips_permanent_sorts_by_id_and_does_not_mutate()
    {
        var since = new DateTime(2026, 9, 26, 6, 59, 0).ToOADate();
        var now = new DateTime(2026, 9, 26, 7, 0, 0).ToOADate();
        var owned = new[]
        {
            V(300, now + 600.0 / 86400),    // soon
            V(200, 0),                       // permanent: never
            V(100, now - 10.0 / 86400),     // expired since the last evaluation
        };

        var due = VehicleRules.Due(owned, since, now);

        Assert.Equal(new[] { (100u, VehicleRules.StateExpired), (300u, VehicleRules.StateExpiringSoon) },
            due.Select(r => (r.RidingId, r.State)).ToArray());
        Assert.Equal(new[] { 300u, 200u, 100u }, owned.Select(v => v.RidingId));
    }

    [DataFact]
    public void NowOle_is_the_local_wall_clock_not_utc()
    {
        var before = DateTime.Now.ToOADate();
        var now = VehiclePackets.NowOle();
        var after = DateTime.Now.ToOADate();
        Assert.InRange(now, before - 1.0 / 86400, after + 1.0 / 86400);
    }

    // ---------------------------------------------------------------------------------------------- reins + extension resolution

    private static readonly RidingDurationGroup[] Groups =
    {
        new() { Item_ID = 1, RidingDuration_ID = 1 },      // permanent reins of group 1
        new() { Item_ID = 2, RidingDuration_ID = 1 },      // 14-day reins of group 1
        new() { Item_ID = 3, RidingDuration_ID = 9 },      // group 9 has no vehicle
        new() { Item_ID = 4, RidingDuration_ID = 2 },      // 7-day reins of group 2
        new() { Item_ID = 5, RidingDuration_ID = 2 },      // Duration_Type 2
        new() { Item_ID = 6, RidingDuration_ID = 2 },      // Duration_Type 1 with Duration_Time 0
        new() { Item_ID = 7, RidingDuration_ID = 2 },      // no ItemCashInfo row
        new() { Item_ID = 1, RidingDuration_ID = 2 },      // a second row for item 1: the FIRST group row wins
    };

    private static readonly RidingInfo[] Vehicles =
    {
        new() { ID = 100, RidingDuration_GroupID = 1, NationEnable = 0 },   // not nation-enabled, still the first row of group 1
        new() { ID = 101, RidingDuration_GroupID = 1, NationEnable = 127 },
        new() { ID = 200, RidingDuration_GroupID = 2, NationEnable = 127 },
    };

    private static readonly ItemCashInfo[] Cash =
    {
        new() { Item_ID = 1, Duration_Type = 0 },
        new() { Item_ID = 2, Duration_Type = 1, Duration_Time = 14 },
        new() { Item_ID = 3, Duration_Type = 0 },
        new() { Item_ID = 4, Duration_Type = 1, Duration_Time = 7 },
        new() { Item_ID = 5, Duration_Type = 2, Duration_Time = 7 },
        new() { Item_ID = 6, Duration_Type = 1, Duration_Time = 0 },
    };

    [DataTheory]
    [InlineData(1u, true, 100u, -1)]        // Duration_Type 0: permanent (days null); first vehicle row of the group, NationEnable not filtered
    [InlineData(2u, true, 100u, 14)]        // Duration_Type 1: Duration_Time days
    [InlineData(4u, true, 200u, 7)]
    [InlineData(3u, false, 0u, -1)]         // group with no vehicle
    [InlineData(5u, false, 0u, -1)]         // Duration_Type other than 0/1
    [InlineData(6u, false, 0u, -1)]         // Duration_Type 1 needs Duration_Time > 0
    [InlineData(7u, false, 0u, -1)]         // no ItemCashInfo row
    [InlineData(8u, false, 0u, -1)]         // not a reins item
    public void TryResolveReins_follows_group_vehicle_and_cash_rows(uint itemId, bool ok, uint ridingId, int days)
    {
        var resolved = VehicleRules.TryResolveReins(Groups, Vehicles, Cash, itemId, out var gotRiding, out var gotDays);

        Assert.Equal(ok, resolved);
        Assert.Equal(ridingId, gotRiding);                                // 0 on every refusal
        Assert.Equal(days < 0 ? (uint?)null : (uint)days, gotDays);
    }

    [DataFact]
    public async Task TryResolveExtension_needs_a_DurationExtendGroup_bit_the_vehicles_reins_accept_and_Duration_Type_1()
    {
        var dir = Directory.CreateTempSubdirectory("ro2-ext-").FullName;
        try
        {
            void Write<T>(string file, IEnumerable<T> rows) => File.WriteAllText(Path.Combine(dir, file), JsonSerializer.Serialize(rows));
            Write("RidingInfo.json", new[]
            {
                new RidingInfo { ID = 100, RidingDuration_GroupID = 1 },
                new RidingInfo { ID = 200, RidingDuration_GroupID = 2 },
            });
            Write("RidingDurationGroup.json", new[]
            {
                new RidingDurationGroup { Item_ID = 11, RidingDuration_ID = 1 },
                new RidingDurationGroup { Item_ID = 12, RidingDuration_ID = 1 },
                new RidingDurationGroup { Item_ID = 21, RidingDuration_ID = 2 },
            });
            Write("ItemCashInfo.json", new[]
            {
                new ItemCashInfo { Item_ID = 11, DurationExtend_GroupID = 3 },       // accepts bits 1|2
                new ItemCashInfo { Item_ID = 12, DurationExtend_GroupID = 4 },       // ... and 4: vehicle 100 accepts 1|2|4
                new ItemCashInfo { Item_ID = 21, DurationExtend_GroupID = 8 },
                new ItemCashInfo { Item_ID = 501, Duration_Type = 1, Duration_Time = 7 },
                new ItemCashInfo { Item_ID = 502, Duration_Type = 1, Duration_Time = 14 },
                new ItemCashInfo { Item_ID = 503, Duration_Type = 1, Duration_Time = 30 },
                new ItemCashInfo { Item_ID = 504, Duration_Type = 0, Duration_Time = 0 },
                new ItemCashInfo { Item_ID = 505, Duration_Type = 1, Duration_Time = 0 },
                new ItemCashInfo { Item_ID = 506, Duration_Type = 1, Duration_Time = 9 },
            });
            Write("DurationExtendGroup.json", new[]
            {
                new DurationExtendGroup { Item_ID = 501, DurationExtend_ID = 1 },
                new DurationExtendGroup { Item_ID = 502, DurationExtend_ID = 4 },
                new DurationExtendGroup { Item_ID = 503, DurationExtend_ID = 8 },
                new DurationExtendGroup { Item_ID = 504, DurationExtend_ID = 1 },    // Duration_Type 0
                new DurationExtendGroup { Item_ID = 505, DurationExtend_ID = 1 },    // Duration_Time 0
                new DurationExtendGroup { Item_ID = 507, DurationExtend_ID = 1 },    // no ItemCashInfo row
                new DurationExtendGroup { Item_ID = 508, DurationExtend_ID = 0 },    // bit 0 never matches
            });
            var store = new GameDataStore();
            await store.LoadAsync(dir);

            (bool ok, uint days) Resolve(uint ridingId, uint itemId)
                => (VehicleRules.ResolveExtension(store.DurationExtendGroup, store.ItemCashInfo, store.RidingInfo, store.RidingDurationGroup, ridingId, itemId, out var d), d);

            Assert.Equal((true, 7u), Resolve(100, 501));      // bit 1 is accepted by reins 11
            Assert.Equal((true, 14u), Resolve(100, 502));     // bit 4 comes from reins 12: the accepted bits are OR-ed across the group's reins
            Assert.Equal((false, 0u), Resolve(100, 503));     // bit 8 belongs to group 2
            Assert.Equal((true, 30u), Resolve(200, 503));
            Assert.Equal((false, 0u), Resolve(200, 501));
            Assert.Equal((false, 0u), Resolve(999, 501));     // unknown vehicle
            Assert.Equal((false, 0u), Resolve(100, 504));     // Duration_Type must be 1
            Assert.Equal((false, 0u), Resolve(100, 505));     // and Duration_Time > 0
            Assert.Equal((false, 0u), Resolve(100, 506));     // not in DurationExtendGroup
            Assert.Equal((false, 0u), Resolve(100, 507));     // no ItemCashInfo row
            Assert.Equal((false, 0u), Resolve(100, 508));     // DurationExtend_ID 0
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---------------------------------------------------------------------------------------------- mount toggle codes + MountService

    [DataFact]
    public void The_1510_result_codes_and_the_2719_body_are_pinned()
    {
        Assert.Equal((ushort)0, RideRidingPetHandler.ResultMounted);
        Assert.Equal((ushort)21801, RideRidingPetHandler.ResultDismounted);
        Assert.Equal((ushort)17, RideRidingPetHandler.ResultSwitchDropped);

        // [u16 result][u32 vehicleId][u32 skillId][u32 0][u32 0], 18 bytes, little endian.
        Assert.Equal("0000" + "a0985d05" + "ad8e5b00" + "00000000" + "00000000",
            Convert.ToHexStringLower(RideRidingPetHandler.BuildAnsRideRidingPet2719(RideRidingPetHandler.ResultMounted, 0x055D98A0, 0x005B8EAD)));
        Assert.Equal("2955" + "a0985d05" + "ad8e5b00" + "00000000" + "00000000",
            Convert.ToHexStringLower(RideRidingPetHandler.BuildAnsRideRidingPet2719(RideRidingPetHandler.ResultDismounted, 0x055D98A0, 0x005B8EAD)));
        Assert.Equal("1100" + "00000000" + "00000000" + "00000000" + "00000000",
            Convert.ToHexStringLower(RideRidingPetHandler.BuildAnsRideRidingPet2719(RideRidingPetHandler.ResultSwitchDropped, 0, 0)));
    }

    [DataFact]
    public void MountService_keeps_one_ride_per_session_replaces_it_and_removes_it_once()
    {
        const uint session = 0xB11A0001;
        Assert.False(MountService.IsMounted(session));
        Assert.Null(MountService.Get(session));
        Assert.Null(MountService.Dismount(session));

        var first = MountService.Mount(session, riderHandle: 77, petId: 6002701, petActorHandle: 90190000);
        Assert.True(MountService.IsMounted(session));
        Assert.Same(first, MountService.Get(session));
        Assert.True(first.Mounted);
        Assert.Equal((77u, 6002701u, 90190000u), (first.RiderHandle, first.PetId, first.PetActorHandle));   // PetId = skill, PetActorHandle = vehicle
        Assert.Contains(first, MountService.Snapshot());

        var second = MountService.Mount(session, 77, 6002301, 90150000);                                    // replaces, no stacking
        Assert.NotSame(first, second);
        Assert.Same(second, MountService.Get(session));
        Assert.DoesNotContain(first, MountService.Snapshot());

        Assert.Same(second, MountService.Dismount(session));
        Assert.Null(MountService.Dismount(session));                                                         // a second drop finds nothing
        Assert.False(MountService.IsMounted(session));
    }

    // ---------------------------------------------------------------------------------------------- glider flight token

    [DataFact]
    public void The_flight_token_lifetime_is_17_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(17), GliderRideStore.FlightTokenLifetime);
    }

    [DataTheory]
    // ticks after the instant the token was issued at (expiry = issue + FlightTokenLifetime)
    [InlineData(0L, true)]
    [InlineData(10_000_000L, true)]                // 1 s
    [InlineData(169_999_999L, true)]               // 17 s - 1 tick
    [InlineData(170_000_000L, true)]               // exactly 17 s: still valid (refused only when now > expiry)
    [InlineData(170_000_001L, false)]              // 17 s + 1 tick: refused
    [InlineData(600_000_000L, false)]
    public void FlightToken_17s_edge(long ticksAfterIssue, bool accepted)
    {
        var player = new PlayerState();
        var issuedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        GliderRideStore.IssueFlightToken(player, 15, issuedAt + GliderRideStore.FlightTokenLifetime);

        Assert.Equal(accepted, GliderRideStore.TryConsumeFlightToken(player, 15, issuedAt.AddTicks(ticksAfterIssue)));
        Assert.False(GliderRideStore.TryConsumeFlightToken(player, 15, issuedAt));    // single use / dropped either way
    }

    [DataFact]
    public void A_later_PutRide_replaces_the_route_and_an_expired_wrong_station_attempt_drops_the_token()
    {
        var player = new PlayerState();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        GliderRideStore.PutRide(player, 3);
        GliderRideStore.PutRide(player, 9);
        Assert.True(GliderRideStore.TryTakeRide(player, out var route));
        Assert.Equal((ushort)9, route);

        // The expiry check runs BEFORE the station check: an expired token is dropped even when the station is the wrong one.
        GliderRideStore.IssueFlightToken(player, 15, t0.AddSeconds(17));
        Assert.False(GliderRideStore.TryConsumeFlightToken(player, 16, t0.AddSeconds(18)));
        Assert.False(GliderRideStore.TryConsumeFlightToken(player, 15, t0));
    }
}
