using System.Collections.Concurrent;
using RO2.Network.Packets;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Field PvP (duel) handshake state: who challenged whom, and whether the challenge is still pending or was accepted.
/// Backs the challenge, accept and cancel messages (1517, 1518 and 1519).
/// </summary>
/// <remarks>
/// <para>
/// The state is static and in-memory. A duel lives only while both of its players are online.
/// </para>
/// <para>
/// Once the start message (2758) has been sent (<see cref="Duel.Started"/>), the two duelists can hit each other with
/// normal attacks and skills, resolved by <see cref="FieldPvPCombat"/>. A hit that would take one of them to 0 HP leaves
/// that duelist at 1 HP and ends the duel with the other duelist as winner. A monster's hit that would take a duelist to 0
/// HP ends it the same way.
/// </para>
/// <para>
/// An accepted duel ends by a forfeit, a cancel, or a player leaving the duel's range. It has no time limit: the players
/// end it by surrendering, cancelling, or closing the duel window.
/// </para>
/// <para>
/// The client measures the duel range itself, and reports an out-of-range loss when its countdown reaches zero. The
/// server only closes the duel and notifies both players.
/// </para>
/// </remarks>
public static class FieldPvPService
{
    public enum DuelState { Pending, Accepted }

    public sealed class Duel
    {
        public uint Id { get; init; }
        public uint MapId { get; init; }
        public uint Challenger { get; init; }
        public uint ChallengerSession { get; init; }
        public uint Target { get; init; }
        public uint TargetSession { get; init; }
        public DuelState State { get; set; }

 /// <summary>
 /// Set when the duel's start message (2758) is sent (<see cref="TryMarkStarted"/>). PvP damage waits for this flag, so an
 /// accepted duel still in its start countdown is not yet a fight.
 /// </summary>
        public bool Started { get; set; }
    }

 /// <summary>
 /// Maximum distance, in 2D world units, between a challenger and its target for a challenge to be sent and for the
 /// target's accept to be taken: 20.
 /// </summary>
 /// <remarks>
 /// <para>
 /// No data table carries this value. It was derived from the players' last reported positions: challenges answered
 /// within 20 units were accepted, and those beyond it were refused.
 /// </para>
 /// </remarks>
    public const float DuelRange = 20f;

 /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> stand within <see cref="DuelRange"/> in 2D.</summary>
    public static bool WithinDuelRange(PlayerState a, PlayerState b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy <= DuelRange * DuelRange;
    }

    private static readonly ConcurrentDictionary<uint, Duel> _duels = new();
    private static readonly Lock _gate = new();
    private static int _lastId;

 /// <summary>
 /// Opens a challenge from <paramref name="challenger"/> to <paramref name="target"/>, or returns null when either of them
 /// already takes part in a live duel. Duel ids come from a server-wide counter.
 /// </summary>
    public static Duel? Open(PlayerState challenger, PlayerState target, PlayerStateManager players)
    {
        lock (_gate)
        {
            if (IsBusy(challenger.EntityId, players) || IsBusy(target.EntityId, players))
                return null;
            var duel = new Duel
            {
                Id = unchecked((uint)Interlocked.Increment(ref _lastId)),
                MapId = challenger.MapId,
                Challenger = challenger.EntityId,
                ChallengerSession = challenger.SessionId,
                Target = target.EntityId,
                TargetSession = target.SessionId,
                State = DuelState.Pending,
            };
            _duels[duel.Id] = duel;
            return duel;
        }
    }

    public static bool TryGet(uint duelId, out Duel duel) => _duels.TryGetValue(duelId, out duel!);

    public static bool TryClose(uint duelId, out Duel duel) => _duels.TryRemove(duelId, out duel!);

 /// <summary>Accepts a pending duel. False when it was no longer pending.</summary>
    public static bool TryAccept(Duel duel)
    {
        lock (_gate)
        {
            if (duel.State != DuelState.Pending || !_duels.ContainsKey(duel.Id)) return false;
            duel.State = DuelState.Accepted;
            return true;
        }
    }

 /// <summary>
 /// Marks <paramref name="duel"/> as started when it is still held and accepted, just before its start message is sent.
 /// False when it was closed in the meantime; then no start message may be sent either.
 /// </summary>
    public static bool TryMarkStarted(Duel duel)
    {
        lock (_gate)
        {
            if (duel.State != DuelState.Accepted || !_duels.TryGetValue(duel.Id, out var held)
                || !ReferenceEquals(held, duel))
                return false;
            duel.Started = true;
            return true;
        }
    }

 /// <summary>
 /// The started duel that <paramref name="entityId"/> is fighting in, or null. The duel must still be live: both players
 /// online and on the duel's map.
 /// </summary>
    public static Duel? StartedDuelOf(uint entityId, PlayerStateManager players)
    {
        lock (_gate)
            return LiveDuelOf(entityId, players) is { State: DuelState.Accepted, Started: true } duel ? duel : null;
    }

 /// <summary>
 /// The state of the live duel that <paramref name="entityId"/> takes part in, as challenger or target, or null when it is
 /// in none. Used to tell a challenger why its target cannot be challenged. Uses the same liveness rule and pruning as
 /// <see cref="Open"/>.
 /// </summary>
    public static DuelState? LiveDuelState(uint entityId, PlayerStateManager players)
    {
        lock (_gate) return LiveDuelOf(entityId, players)?.State;
    }

 /// <summary>
 /// Whether <paramref name="entityId"/> is in a duel whose two players are both still online and on the duel's map.
 /// </summary>
    private static bool IsBusy(uint entityId, PlayerStateManager players) => LiveDuelOf(entityId, players) is not null;

 /// <summary>
 /// The duel that <paramref name="entityId"/> is in, if both of its players are still online and on the duel's map. A duel
 /// that fails this check is dropped, since nothing else ends it when a player logs out or changes map.
 /// </summary>
    private static Duel? LiveDuelOf(uint entityId, PlayerStateManager players)
    {
        foreach (var duel in _duels.Values)
        {
            if (duel.Challenger != entityId && duel.Target != entityId) continue;
            if (IsPresent(players, duel.ChallengerSession, duel.Challenger, duel.MapId)
                && IsPresent(players, duel.TargetSession, duel.Target, duel.MapId))
                return duel;
            _duels.TryRemove(duel.Id, out _);
        }
        return null;
    }

    private static bool IsPresent(PlayerStateManager players, uint sessionId, uint entityId, uint mapId)
        => players.GetPlayer(sessionId) is { } p && p.EntityId == entityId && p.MapId == mapId;
}

/// <summary>
/// Server-to-client bodies of the field PvP family. Each is a compact message whose fields start at the recipient's own
/// handle, so each message is built once per recipient.
/// </summary>
public static class FieldPvPPackets
{
 /// <summary>2735 AnsCertifyFieldPvP: msgTag 58455, body 20 bytes.</summary>
    internal const ushort MsgTagCertify = 58455;
 /// <summary>2737 NfyAcceptFiledPvP: msgTag 58456, body 34 bytes.</summary>
    internal const ushort MsgTagAccept = 58456;
 /// <summary>2738 NfyCancelFiledPvP: msgTag 58457, body 18 bytes.</summary>
    internal const ushort MsgTagCancel = 58457;
 /// <summary>2736 NfyResultFiledPvP: msgTag 58458, body 18 bytes.</summary>
    internal const ushort MsgTagResult = 58458;

 /// <summary>2737 result 12: the target accepted. Carries the target's position.</summary>
    internal const ushort AcceptAccepted = 12;
 /// <summary>2737 result 13: the target refused.</summary>
    internal const ushort AcceptRefused = 13;
 /// <summary>
 /// 2737 result 14, sent to both players: a duel that was still held was ended by either player's answer 3 (cancel). The
 /// challenger and the target each see their own cancel notice.
 /// </summary>
    internal const ushort AcceptWithdrawn = 14;
 /// <summary>
 /// 2737 result 23: a Battle System error, sent to the sender alone for an answer about a duel that is no longer open.
 /// Never sent for a duel that is still held.
 /// </summary>
    internal const ushort AcceptAcknowledged = 23;
 /// <summary>
 /// 2737 result: the challenger's challenge timer ran out while the duel was still held. Sent to the challenger.
 /// </summary>
    internal const ushort AcceptTimedOut = 28;
 /// <summary>
 /// 2737 result 18: the target's accept was refused because the two players stand farther apart than
 /// <see cref="FieldPvPService.DuelRange"/>. Sent with a zero position to both players, followed by the end state pair.
 /// </summary>
    internal const ushort AcceptTooFar = 18;

 /// <summary>2738 u16: 3, for a cancel (1519 code 3).</summary>
    internal const ushort CancelCode = 3;
 /// <summary>2736 u16 after a forfeit (1519 code 5): 4.</summary>
    internal const ushort ResultForfeit = 4;
 /// <summary>
 /// 2736 u16 when a duelist's out-of-range countdown ran out and its client reported it: 1, with that duelist as the loser.
 /// </summary>
    internal const ushort ResultOutOfRange = 1;
 /// <summary>
 /// 2736 u16 when the duel was fought out: 3. The loser is the duelist whose HP a hit would have taken to 0 (it is left at
 /// 1 HP instead). See <see cref="FieldPvPCombat.EndFoughtOutAsync"/>.
 /// </summary>
    internal const ushort ResultFoughtOut = 3;

 /// <summary>
 /// 2735 AnsCertifyFieldPvP, the challenge, sent to both players: <c>[u32 self][u32 challenger][u32 target][u32 duelId]</c>.
 /// </summary>
    public static byte[] BuildCertify2735(uint self, uint challenger, uint target, uint duelId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagCertify);
            w.WriteUInt32(self);
            w.WriteUInt32(challenger);
            w.WriteUInt32(target);
            w.WriteUInt32(duelId);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// 2737 NfyAcceptFiledPvP, the outcome of the challenge:
 /// <c>[u32 self][u32 duelId][u32 challenger][u32 target][f32 x][f32 y][f32 z][u16 result]</c>.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The position is non-zero only for result 12, where it is the target's own position at the moment of accepting. Every
 /// other result carries zeros.
 /// </para>
 /// </remarks>
    public static byte[] BuildAccept2737(uint self, uint duelId, uint challenger, uint target,
        float x, float y, float z, ushort result)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagAccept);
            w.WriteUInt32(self);
            w.WriteUInt32(duelId);
            w.WriteUInt32(challenger);
            w.WriteUInt32(target);
            w.WriteSingle(x);
            w.WriteSingle(y);
            w.WriteSingle(z);
            w.WriteUInt16(result);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// 2738 NfyCancelFiledPvP, a challenge cancelled with 1519 code 3, sent to both players:
 /// <c>[u32 self][u32 duelId][u16 3][u32 canceller]</c>.
 /// </summary>
    public static byte[] BuildCancel2738(uint self, uint duelId, uint canceller)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagCancel);
            w.WriteUInt32(self);
            w.WriteUInt32(duelId);
            w.WriteUInt16(CancelCode);
            w.WriteUInt32(canceller);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// 2736 NfyResultFiledPvP, how a duel ended: <c>[u32 self][u32 winner][u32 loser][u16 code]</c>.
 /// </summary>
    public static byte[] BuildResult2736(uint self, uint winner, uint loser, ushort code)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagResult);
            w.WriteUInt32(self);
            w.WriteUInt32(winner);
            w.WriteUInt32(loser);
            w.WriteUInt16(code);
        });
        return pw.ToArray();
    }
}
