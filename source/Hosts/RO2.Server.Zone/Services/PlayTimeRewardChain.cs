using System.Buffers.Binary;
using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Per-account progress through the play-time reward chain. Shared by all characters of the account
/// and persisted by <see cref="PlayTimeRewardStore"/>.
/// </summary>
public sealed class PlayTimeRewardProgress
{
 /// <summary>Lock that serializes polls, claims and clock ticks for one account.</summary>
    public readonly Lock Gate = new();

 /// <summary>Index of the chain row currently being worked towards.</summary>
    public int Index { get; set; }

 /// <summary>Online seconds counted towards that row since the last claim or reset.</summary>
    public double AccumulatedSeconds { get; set; }

 /// <summary>Start (UTC) of the daily reset period this progress belongs to.</summary>
    public DateTime PeriodStartUtc { get; set; }

 /// <summary>Last instant (UTC) whose online time is already counted in <see cref="AccumulatedSeconds"/>.</summary>
    public DateTime LastTickUtc { get; set; }
}

/// <summary>
/// The play-time reward chain: row progression, claim rules and the daily reset.
/// </summary>
/// <remarks>
/// The chain is built from the Type-1 rows of Attendance.json. Progress belongs to the account, not the
/// character: all characters of an account share one chain. Only in-world time counts; time spent in
/// character select does not. After the last row the chain restarts from the first, and a daily reset
/// returns it to the first row with nothing counted.
/// </remarks>
public static class PlayTimeRewardChain
{
 /// <summary>Result code for an answer that names the next slot.</summary>
    public const ushort ResultOk = 0;

 /// <summary>Result code answering the claim of the last row; the chain then restarts from the first row.</summary>
    public const ushort ResultChainComplete = 31002;

 /// <summary>Result code for a claim that is early or names another slot. Carries the current slot and its remaining seconds.</summary>
    public const ushort ResultNotYet = 31004;

 /// <summary>The daily reset instant as a UTC time of day.</summary>
    public static readonly TimeSpan ResetTimeOfDayUtc = TimeSpan.FromHours(12);

 /// <summary>Attendance.Type of the play-time rows (type 0 holds the daily attendance rows).</summary>
    private const byte PlayTimeRowType = 1;

 /// <summary>Outcome of a claim: the result to send and the row whose rewards must be delivered, or null if none.
 /// Marks a ready claim refused because its reward could not be placed.</summary>
    public readonly record struct ClaimResult(ushort Result, uint Slot, uint Seconds, Attendance? GrantedRow, bool Undeliverable = false);

 /// <summary>The chain rows in claim order.</summary>
    public static Attendance[] Chain(GameDataStore store) => Chain(store.Attendance);

 /// <summary>The Type-1 rows of <paramref name="rows"/>, ordered by ID.</summary>
    public static Attendance[] Chain(IEnumerable<Attendance> rows) =>
        rows.Where(r => r.Type == PlayTimeRowType).OrderBy(r => r.ID).ToArray();

 /// <summary>The most recent reset instant at or before <paramref name="nowUtc"/>.</summary>
    public static DateTime PeriodStart(DateTime nowUtc)
    {
        var today = DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc) + ResetTimeOfDayUtc;
        return nowUtc >= today ? today : today.AddDays(-1);
    }

 /// <summary>The first reset instant strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTime NextResetAfter(DateTime nowUtc) => PeriodStart(nowUtc).AddDays(1);

 /// <summary>Initial progress for a chain not yet played in the current period: first row, nothing counted.</summary>
    public static PlayTimeRewardProgress NewProgress(DateTime loginUtc, DateTime nowUtc) => new()
    {
        Index = 0,
        AccumulatedSeconds = 0,
        PeriodStartUtc = PeriodStart(nowUtc),
        LastTickUtc = loginUtc,
    };

 /// <summary>
 /// Advances <paramref name="p"/> to <paramref name="nowUtc"/> for a character of the account that has been
 /// in world since <paramref name="loginUtc"/>. Only time since that login counts. A crossed reset instant
 /// returns the chain to its first row. Caller holds <see cref="PlayTimeRewardProgress.Gate"/>.
 /// </summary>
 /// <remarks>
 /// The tick only moves forward. If two characters of the account are in world at once, each one counts
 /// only the time the other has not already counted.
 /// </remarks>
    public static void Advance(PlayTimeRewardProgress p, DateTime loginUtc, DateTime nowUtc)
    {
        if (p.LastTickUtc < loginUtc)
            p.LastTickUtc = loginUtc;

        var period = PeriodStart(nowUtc);
        if (p.PeriodStartUtc < period)
        {
            p.Index = 0;
            p.AccumulatedSeconds = 0;
            p.PeriodStartUtc = period;
            if (p.LastTickUtc < period)
                p.LastTickUtc = period;
        }

        if (nowUtc > p.LastTickUtc)
        {
            p.AccumulatedSeconds += (nowUtc - p.LastTickUtc).TotalSeconds;
            p.LastTickUtc = nowUtc;
        }
    }

 /// <summary>
 /// Returns the current (slot, remaining seconds) for a 2944, or null when the data has no play-time row.
 /// Caller holds <see cref="PlayTimeRewardProgress.Gate"/>.
 /// </summary>
    public static (uint Slot, uint RemainingSeconds)? Peek(PlayTimeRewardProgress p, Attendance[] chain, DateTime loginUtc, DateTime nowUtc)
    {
        if (chain.Length == 0) return null;
        Advance(p, loginUtc, nowUtc);
        var row = chain[ClampIndex(p, chain)];
        return (row.ID, Remaining(row, p));
    }

 /// <summary>
 /// Resolves a 1643 claim for <paramref name="requestedSlot"/>, advancing the chain when the claim is granted.
 /// The last row wraps back to the first. Returns null when the data has no play-time row. Caller holds
 /// <see cref="PlayTimeRewardProgress.Gate"/>, delivers <see cref="ClaimResult.GrantedRow"/> and sends the 2945.
 /// </summary>
 /// <param name="canDeliver">Called only for a ready claim, before any state changes. Returning false leaves the
 /// chain unchanged so the slot stays claimable; needed because the reward goes to the bag, which can be full.</param>
    public static ClaimResult? Claim(PlayTimeRewardProgress p, Attendance[] chain, uint requestedSlot, DateTime loginUtc,
        DateTime nowUtc, Func<Attendance, bool> canDeliver)
    {
        if (chain.Length == 0) return null;
        Advance(p, loginUtc, nowUtc);
        var row = chain[ClampIndex(p, chain)];
        uint remaining = Remaining(row, p);
        if (requestedSlot != row.ID || remaining > 0)
            return new ClaimResult(ResultNotYet, row.ID, remaining, null);

        
        
        if (!canDeliver(row))
            return new ClaimResult(ResultNotYet, row.ID, 0, null, Undeliverable: true);

        p.Index = (p.Index + 1) % chain.Length;
        p.AccumulatedSeconds = 0;
        if (p.Index == 0)
            return new ClaimResult(ResultChainComplete, 0, 0, row);
        var next = chain[p.Index];
        return new ClaimResult(ResultOk, next.ID, next.Time, row);
    }

 /// <summary>
 /// Body of 2944 AnsPlayTimeReward and 2945 AnsPlayTimeRewardGetItem: fixed 10 bytes,
 /// <c>[u16 result][u32 slot][u32 seconds]</c>.
 /// </summary>
    public static byte[] BuildAnswer(ushort result, uint slot, uint seconds)
    {
        var body = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), slot);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), seconds);
        return body;
    }

 /// <summary>Converts a stored slot id back to its chain index; 0 when the row no longer exists in the data.</summary>
    public static int IndexOfSlot(Attendance[] chain, uint slotId)
    {
        for (int i = 0; i < chain.Length; i++)
            if (chain[i].ID == slotId) return i;
        return 0;
    }

    private static int ClampIndex(PlayTimeRewardProgress p, Attendance[] chain)
    {
        if (p.Index < 0 || p.Index >= chain.Length)
            p.Index = 0;
        return p.Index;
    }

    private static uint Remaining(Attendance row, PlayTimeRewardProgress p) =>
        (uint)Math.Max(0, Math.Ceiling(row.Time - p.AccumulatedSeconds));
}
