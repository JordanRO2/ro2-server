using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Which actors a non-damaging 2718 NfySkillDamage lists, by the skill's Skill_Table_Base.Skill_Type: the PC elements
/// (vitals, no effect record) of buff / debuff casts, the lone all-zero element of a type-10 cast at one PC, and the
/// NPC rule (only an attack skill damages a monster). Pure, so SkillDamageHandler's element loop can be tested.
/// </summary>
/// <remarks>
/// <para>
/// "Named" is the request's primary target (when blob+4 is 1) followed by its hit list. By the request skill's
/// Skill_Type (group = skill / 100), the 2718 of a cast lists:
/// </para>
/// <list type="bullet">
/// <item>Type 4 (buff): each named PC, the caster included, with its current HP and SP and NO effect record. Named NPCs
/// are never an element.</item>
/// <item>Type 2 (debuff): the named PCs other than the caster (vitals, no effect record), and each named NPC with its
/// vitals and no effect record (no damage).</item>
/// <item>Type 10 (self): when the request names exactly one actor and it is a PC, ONE element with every field zero
/// (handle 0, HP 0, SP 0, no effect). Named NPCs are never an element.</item>
/// <item>Type 3 (heal): PC elements carry negative damage and NPC elements effect records with no damage; this server
/// has no heal model, so a type-3 cast lists nothing.</item>
/// <item>Type 1 (attack): a PC element carries damage, which is PvP; SkillDamageHandler keeps its started-duel rule.
/// Named NPCs are damaged.</item>
/// </list>
/// <para>
/// A skill whose type the table does not give (an unknown group, or Skill_Table_Base unreadable, <see
/// cref="SkillDamagePackets.SkillTypeTableError"/>) damages the monsters it names and lists no PC.
/// </para>
/// </remarks>
internal static class SkillDamagePcElements
{
    /// <summary>Skill_Table_Base.Skill_Type values the rules name.</summary>
    internal const int TypeAttack = 1, TypeDebuff = 2, TypeHeal = 3, TypeBuff = 4, TypeSelf = 10;

    /// <summary>Selects the actor type tag, the low byte of an actor handle.</summary>
    private const uint ActorTagMask = 0xFF;

    /// <summary>Width in bits of the actor type tag; the session counter of a PC handle sits above it.</summary>
    private const int ActorHandleTagBits = 8;

    /// <summary>The largest SP a 2718 element carries: the client reads the field as a 16-bit value.</summary>
    private const int MaxElementSp = ushort.MaxValue;

    /// <summary>
    /// True when a cast of this type damages the monsters it names: an attack skill, or a skill whose type is unknown.
    /// </summary>
    internal static bool DamagesNpcs(int? skillType) => skillType is null or TypeAttack;

    /// <summary>True when a cast of this type lists each named NPC with its vitals and no effect record (type 2).</summary>
    internal static bool ListsNpcVitals(int? skillType) => skillType == TypeDebuff;

    /// <summary>
    /// True when the 2718 of this cast is the single all-zero element: a type-10 skill whose request named exactly
    /// one actor, a PC (actor tag <see cref="PlayerStateManager.PcActorTag"/>, 0x11).
    /// </summary>
    internal static bool IsSingleZeroElementCast(int? skillType, IReadOnlyList<uint> named)
        => skillType == TypeSelf && named.Count == 1 && (named[0] & ActorTagMask) == PlayerStateManager.PcActorTag;

    /// <summary>The all-zero element of <see cref="IsSingleZeroElementCast"/>: handle 0, HP 0, SP 0, no effect record.</summary>
    internal static SkillDamageHit SingleZeroElement() => new(0, 0, 0, null);

    /// <summary>
    /// The element a named PC gets from a cast of this type, or null when it gets none: types 4 (every PC) and 2 (every
    /// PC but the caster) list the PC's current HP and SP with no effect record. <paramref name="pc"/> is the PC the
    /// handle resolved to on the caster's map.
    /// </summary>
    internal static SkillDamageHit? PcElement(int? skillType, PlayerState caster, PlayerState pc)
    {
        if (skillType == TypeBuff || (skillType == TypeDebuff && pc.EntityId != caster.EntityId))
            return new SkillDamageHit(pc.EntityId, (uint)Math.Max(0, pc.Hp), (uint)Math.Clamp(pc.Mp, 0, MaxElementSp), null);
        return null;
    }

    /// <summary>
    /// The PC <paramref name="handle"/> names on <paramref name="caster"/>'s map (the caster included), or null.
    /// A PC handle is <c>(sessionId &lt;&lt; 8) | 0x11</c> (<see cref="PlayerStateManager.BuildActorHandle"/>).
    /// </summary>
    internal static PlayerState? ResolvePc(PlayerStateManager players, PlayerState caster, uint handle)
    {
        if ((handle & ActorTagMask) != PlayerStateManager.PcActorTag) return null;
        if (handle == caster.EntityId) return caster;
        return players.GetPlayer(handle >> ActorHandleTagBits) is { } pc && pc.EntityId == handle && pc.MapId == caster.MapId ? pc : null;
    }
}
