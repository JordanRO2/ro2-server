using System.Collections.Concurrent;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Per-character progress through the play-time reward chain, polled by 1642 and claimed by 1643.
/// </summary>
/// <remarks>
/// The chain is built from the Type-1 rows of Attendance.json. The remaining time shown for a slot is
/// measured against the clock, which restarts at each claim and pauses while the character is not in the
/// world. Progress is kept in a process-lifetime registry keyed by character id, so the poll (1642) and the
/// claim (1643), which sit on different base classes, read the same state.
/// </remarks>
public static class PlayTimeRewardState
{
 /// <summary>Result code for a granted claim that has a next slot.</summary>
    public const ushort ResultOk = 0;

 /// <summary>Result code answering the claim of the last slot. The chain then restarts from the first slot.</summary>
    public const ushort ResultChainComplete = 31002;

 /// <summary>Result code for a claim that is early or names the wrong slot. Carries the current slot and its remaining seconds.</summary>
    public const ushort ResultNotYet = 31004;

 /// <summary>Attendance.Type of the play-time rows (type 0 holds the daily attendance rows).</summary>
    private const byte PlayTimeRowType = 1;

    private sealed class Progress
    {
        public readonly Lock Gate = new();
        public int Index;
        public double AccumulatedSeconds;
        public DateTime LastTickUtc;
        public DateTime OnlineSinceUtc;
    }

    private static readonly ConcurrentDictionary<uint, Progress> ByCharacter = new();

 /// <summary>Outcome of a claim: the result to send and the row whose rewards must be delivered, or null if none.</summary>
    public readonly record struct ClaimResult(ushort Result, uint Slot, uint Seconds, Attendance? GrantedRow);

 /// <summary>The chain rows in claim order.</summary>
    public static Attendance[] Chain(GameDataStore store) =>
        store.Attendance.Where(r => r.Type == PlayTimeRowType).OrderBy(r => r.ID).ToArray();

 /// <summary>Returns the current (slot, remaining seconds) for a 2944 poll answer; (0, 0) once the chain is finished.</summary>
    public static (uint Slot, uint RemainingSeconds) Peek(PlayerState player, GameDataStore store)
    {
        var chain = Chain(store);
        var p = ByCharacter.GetOrAdd(player.CharacterId, _ => New(player));
        lock (p.Gate)
        {
            Tick(p, player);
            if (p.Index >= chain.Length) return (0, 0);
            var row = chain[p.Index];
            return (row.ID, Remaining(row, p));
        }
    }

 /// <summary>
 /// Resolves a 1643 claim for <paramref name="requestedSlot"/> and advances the chain when it is granted.
 /// The caller delivers the rewards of <see cref="ClaimResult.GrantedRow"/> and sends the 2945.
 /// </summary>
    public static ClaimResult Claim(PlayerState player, GameDataStore store, uint requestedSlot)
    {
        var chain = Chain(store);
        var p = ByCharacter.GetOrAdd(player.CharacterId, _ => New(player));
        lock (p.Gate)
        {
            Tick(p, player);
            if (p.Index >= chain.Length)
                return new ClaimResult(ResultChainComplete, 0, 0, null);

            var row = chain[p.Index];
            uint remaining = Remaining(row, p);
            if (requestedSlot != row.ID || remaining > 0)
                return new ClaimResult(ResultNotYet, row.ID, remaining, null);

            p.Index++;
            p.AccumulatedSeconds = 0;
            if (p.Index >= chain.Length)
                return new ClaimResult(ResultChainComplete, 0, 0, row);
            var next = chain[p.Index];
            return new ClaimResult(ResultOk, next.ID, next.Time, row);
        }
    }

    private static Progress New(PlayerState player) => new()
    {
        LastTickUtc = player.LoginTime,
        OnlineSinceUtc = player.LoginTime,
    };

 /// <summary>
 /// Adds the online time since the last observation. A new PlayerState (new login) restarts the stint at its
 /// LoginTime, so offline time is never counted.
 /// </summary>
    private static void Tick(Progress p, PlayerState player)
    {
        if (p.OnlineSinceUtc != player.LoginTime)
        {
            p.OnlineSinceUtc = player.LoginTime;
            p.LastTickUtc = player.LoginTime;
        }
        var now = DateTime.UtcNow;
        if (now > p.LastTickUtc)
            p.AccumulatedSeconds += (now - p.LastTickUtc).TotalSeconds;
        p.LastTickUtc = now;
    }

    private static uint Remaining(Attendance row, Progress p) =>
        (uint)Math.Max(0, Math.Ceiling(row.Time - p.AccumulatedSeconds));
}
