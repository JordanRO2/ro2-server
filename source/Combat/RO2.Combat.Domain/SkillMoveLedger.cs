using System.Collections.Concurrent;

namespace RO2.Combats.Domain;

/// <summary>
/// One caster's state of the ground-object skill moves: the target of the caster's accepted cast of each skill
/// (so a 1523 may move it at all), and which actor each skill is dragging and where 1523 ReqSkillMoveNotify last
/// put it. 1525 ReqSkillMoveEnd takes the move entry, so it knows which actor the move ends on. The host holds
/// one instance per player (lifetime = the player; a logout leaves nothing behind) and owns the reach table that
/// bounds how far a move may reach.
/// </summary>
/// <remarks>
/// Both the cast record and the move record are kept per skill id (group*100 + level); a cast of another skill
/// does not evict a cast, and a 1525 removes a move. Each is capped so a client cannot grow the maps for ever.
/// </remarks>
public sealed class SkillMoveLedger
{
    /// <summary>The actor a skill is moving, the move kind and the last position 1523 reported.</summary>
    public readonly record struct SkillMove(uint MovedHandle, byte MoveKind, float X, float Y, float Z);

    /// <summary>An accepted 1508: its skill id, its +16 target, the map it was cast on and when (only to bound the dictionary).</summary>
    public readonly record struct Cast(uint MapId, uint SkillId, uint TargetHandle, DateTime AtUtc);

    /// <summary>
    /// The most casts held per caster. A 1508 names a skill by a client u32, so without a cap a client could grow the
    /// dictionary for ever; past it the oldest cast (by <see cref="Cast.AtUtc"/>) is evicted.
    /// A server-side bound: the number of distinct skills one PC casts inside a move's lifetime is not data-driven.
    /// </summary>
    public const int MaxCastsPerCaster = 64;

    /// <summary>
    /// The most moves held per caster: the 32 of <see cref="CastLedger.MaxPendingPerPlayer"/>. A 1523 names a
    /// skill by a client u32, and only a 1525 removes the entry, so without a cap a client could grow it for ever.
    /// A server-side bound: the number of ground-object skills one PC has moving at once is not data-driven.
    /// </summary>
    public const int MaxMovesPerCaster = CastLedger.MaxPendingPerPlayer;

    private readonly ConcurrentDictionary<uint, Cast> _casts = new();
    private readonly ConcurrentDictionary<uint, SkillMove> _moves = new();

    /// <summary>
    /// Records the accepted 1508 ReqSkillStart of <paramref name="skillId"/> on <paramref name="targetHandle"/>
    /// (request +16) as the caster's cast of that skill, replacing the previous cast of the SAME skill (a cast of
    /// another skill does not evict it: the ground effect of a move skill outlives the next cast). <paramref
    /// name="nowUtc"/> only bounds the dictionary (oldest evicted past the cap).
    /// </summary>
    public void RecordCast(uint mapId, uint skillId, uint targetHandle, DateTime nowUtc)
    {
        _casts[skillId] = new Cast(mapId, skillId, targetHandle, nowUtc);
        while (_casts.Count > MaxCastsPerCaster)
        {
            var oldest = _casts.MinBy(kv => kv.Value.AtUtc).Key;
            _casts.TryRemove(oldest, out _);
        }
    }

    /// <summary>
    /// Whether <paramref name="handle"/> is the target of the caster's accepted cast of exactly
    /// <paramref name="skillId"/>, made on <paramref name="mapId"/>. A zero handle never is.
    /// </summary>
    public bool IsCastTarget(uint mapId, uint skillId, uint handle)
        => handle != 0 && _casts.TryGetValue(skillId, out var cast)
            && cast.MapId == mapId && cast.TargetHandle == handle;

    /// <summary>
    /// Whether the caster has an accepted cast of exactly <paramref name="skillId"/> made on
    /// <paramref name="mapId"/>, whatever its target (the self-move check of 1523/1525).
    /// </summary>
    public bool IsCastSkill(uint mapId, uint skillId)
        => _casts.TryGetValue(skillId, out var cast) && cast.MapId == mapId;

    /// <summary>How many moves the caster holds.</summary>
    public int MoveCount() => _moves.Count;

    /// <summary>
    /// Records (or replaces) the move of <paramref name="skillId"/>. A skill already held is always replaced; a
    /// new one is refused (false, nothing stored) once the caster holds <see cref="MaxMovesPerCaster"/>.
    /// </summary>
    public bool Record(uint skillId, SkillMove move)
    {
        lock (_moves)
        {
            if (!_moves.ContainsKey(skillId) && _moves.Count >= MaxMovesPerCaster)
                return false;
            _moves[skillId] = move;
            return true;
        }
    }

    /// <summary>Removes and returns the move of <paramref name="skillId"/>; false when no 1523 recorded one.</summary>
    public bool TryTake(uint skillId, out SkillMove move) => _moves.TryRemove(skillId, out move);

    /// <summary>
    /// The move kinds for which the client keeps the cast target as the dragged actor: 9, 14, 17, 20 and 22.
    /// Under any other kind there is no dragged actor, so a 1523 naming an NPC under it cannot come from the
    /// client. The kind is the client's claim, carried in the same request: a consistency check, not proof.
    /// </summary>
    public static bool IsTargetMoveKind(byte moveKind) => moveKind is 9 or 14 or 17 or 20 or 22;
}
