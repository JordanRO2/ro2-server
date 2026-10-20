using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Fixed rules of the Illusion Pyramid: the reward of every floor slot, the odds of each slot, and the three
/// ways to enter a run (Phantom Stone, Mystical or Bright).
/// </summary>
/// <remarks>
/// <para>
/// Rewards are listed in a 3011 catalog body of 42 records, each holding the item, floor, slot, count, Mileage
/// id, an end-of-run flag and a broadcast flag. Floors 1 to 7 have 9, 8, 7, 6, 5, 4 and 3 slots, and every slot
/// of the top floor ends the run. A record with Mileage id 1 or 2 pays that many Mystical or Bright Phantom
/// Stones; the others pay the item.
/// </para>
/// <para>
/// Slot odds are fixed weights per (floor, slot) and do not depend on the entry kind.
/// </para>
/// <para>
/// A run costs its entry once when it settles: one Phantom Stone, or 10 Mystical or Bright stones.
/// </para>
/// </remarks>
public static class IllusionPyramidCatalog
{
 /// <summary>The 3011 catalog body, in hex.</summary>
    public const string Body3011Hex =
        "0000012a8bc4c901010101010100dad355010102010000005587930301030100000096c4c901010401000000dad35501" +
        "0105010000005f7845010106010000008a899303010701000000dd8a93030108010000008bc4c9010109010101008bc4" +
        "c90102010101010096c4c901020201000000f489930302030100000087b14f010204010000009cc4c901020501000000" +
        "208a930302060500000099c4c9010207010000008bc4c9010208020101008bc4c90103010201010099c4c90103020300" +
        "000006889303030301000000038b9303030401000000aec4c901030501000000607845010306030000008bc4c9010307" +
        "020101008bc4c901040102010100e689930304020500000095242c04040301000000cb8793030404010000000f202c04" +
        "04050a0000008cc4c9010406010201008cc4c90105010102010013202c0405020a0000007987930305030a0000008288" +
        "93030504010000008cc4c9010505010201008cc4c901060101020100f5212c04060201000001ca879303060303000001" +
        "8cc4c9010604020201008cc4c9010701020201005c3cf8030702010001018cc4c901070302020100";

 /// <summary>One slot of one floor: what it pays and whether it ends the run.</summary>
    public sealed record Reward(uint ItemId, byte Floor, byte Slot, byte Count, byte MileageId, bool End, bool Broadcast)
    {
 /// <summary>True when the slot pays Mystical or Bright Phantom Stones instead of an item.</summary>
        public bool IsMileage => MileageId != 0;
    }

 /// <summary>How a run was entered.</summary>
    public enum EntryKind : byte { None = 0, PhantomStone = 1, Mystical = 2, Bright = 3 }

 /// <summary>The Phantom Stone item id.</summary>
    public const uint PhantomStoneItemId = 60000377;

 /// <summary>Mileage id of the Mystical Phantom Stone.</summary>
    public const byte MysticalMileageId = 1;

 /// <summary>Mileage id of the Bright Phantom Stone.</summary>
    public const byte BrightMileageId = 2;

 /// <summary>Number of Mystical or Bright stones that one entry costs.</summary>
    public const int MileageEntryCost = 10;

 /// <summary>Maximum number of Mystical or Bright stones a player can hold.</summary>
    public const int MileageCap = 999;

 /// <summary>The last floor. Every slot of it ends the run.</summary>
    public const byte TopFloor = 7;

 /// <summary>Bytes in one 3011 record.</summary>
    private const int RecordBytes = 10;

 /// <summary>
 /// Result weight of every (floor, slot), floor 1 first, slot 1 first. Used as the roll's weights.
 /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<int>> SlotWeights = new int[][]
    {
        new[] { 11321, 12929, 12604, 12915, 12847, 12746, 12857, 12858, 11307 },
        new[] { 12985, 9685, 9639, 9681, 9840, 9782, 9806, 13198 },
        new[] { 12874, 7386, 8499, 6788, 8510, 7388, 12824 },
        new[] { 6877, 3739, 3936, 3622, 3780, 6826 },
        new[] { 3885, 2053, 2177, 2136, 3782 },
        new[] { 1812, 1143, 1154, 1855 },
        new[] { 380, 361, 337 },
    };

    private static readonly Lazy<IReadOnlyList<Reward>> s_rewards = new(() => Parse3011(Convert.FromHexString(Body3011Hex)));

 /// <summary>The 42 rewards of the catalog, in order (floor, then slot).</summary>
    public static IReadOnlyList<Reward> Rewards => s_rewards.Value;

 /// <summary>Returns the reward of (<paramref name="floor"/>, <paramref name="slot"/>), or null.</summary>
    public static Reward? Find(byte floor, byte slot)
    {
        foreach (var r in Rewards)
            if (r.Floor == floor && r.Slot == slot) return r;
        return null;
    }

 /// <summary>
 /// Returns the entry kind of a start request: a Phantom Stone, a Mystical stone or a Bright stone. Returns
 /// None for anything else.
 /// </summary>
    public static EntryKind KindOf(byte mode, uint costRef) => (mode, costRef) switch
    {
        (IllusionPyramidService.ModeStoneStart, PhantomStoneItemId) => EntryKind.PhantomStone,
        (IllusionPyramidService.ModeMileageStart, MysticalMileageId) => EntryKind.Mystical,
        (IllusionPyramidService.ModeMileageStart, BrightMileageId) => EntryKind.Bright,
        _ => EntryKind.None,
    };

 /// <summary>Returns the floor a run of <paramref name="kind"/> starts on: 1, 3 or 5, or 0 for None.</summary>
    public static byte EntryFloor(EntryKind kind) => kind switch
    {
        EntryKind.PhantomStone => 1,
        EntryKind.Mystical => 3,
        EntryKind.Bright => 5,
        _ => 0,
    };

 /// <summary>
 /// Rolls the reward of <paramref name="floor"/> with the slot weights.
 /// <paramref name="nextInt"/> returns a number in [0, its argument).
 /// </summary>
    public static Reward Roll(byte floor, Func<int, int> nextInt)
    {
        if (floor < 1 || floor > TopFloor) throw new ArgumentOutOfRangeException(nameof(floor));
        var weights = SlotWeights[floor - 1];
        int pick = nextInt(weights.Sum());
        for (int i = 0; i < weights.Count; i++)
        {
            if (pick < weights[i]) return Find(floor, (byte)(i + 1))!;
            pick -= weights[i];
        }
        return Find(floor, (byte)weights.Count)!;
    }

 /// <summary>Parses the records of a 3011 body.</summary>
    public static IReadOnlyList<Reward> Parse3011(ReadOnlySpan<byte> body)
    {
        
        int count = body[3];
        var list = new List<Reward>(count);
        for (int i = 0; i < count; i++)
        {
            var r = body.Slice(4 + i * RecordBytes, RecordBytes);
            list.Add(new Reward(BinaryPrimitives.ReadUInt32LittleEndian(r), r[4], r[5], r[6], r[7], r[8] != 0, r[9] != 0));
        }
        return list;
    }
}
