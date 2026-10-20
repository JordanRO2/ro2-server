using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Models;

/// <summary>
/// A buff or debuff currently applied to a player, tracked by <see cref="RO2.Server.Zone.Services.EffectService"/>.
/// The fields mirror the payload of RMI 2492 OnAddEffect.
/// </summary>
/// <remarks>
/// The effect ID, source skill ID, and duration are taken from the item or skill data, so runtime state carries
/// no invented values. See <see cref="RO2.Server.Zone.Services.EffectService.PlanItemEffect"/>.
/// </remarks>
public sealed class ActiveEffect
{
 /// <summary>Effect table ID applied. Written at record offset +12 of RMI 2492 and +8 of 2493/2494.</summary>
    public required uint EffectId { get; init; }

 /// <summary>Skill level table ID that granted the effect, or 0. Written at record offset +27 of RMI 2492.</summary>
    public uint SourceSkillId { get; init; }

 /// <summary>Magnitude of the effect, as sent at record offset +16 of RMI 2492.</summary>
    public ushort EffectValue { get; init; }

 /// <summary>Stack count, from 1 to 100. Written at record offset +26 of RMI 2492.</summary>
    public byte StackCount { get; init; } = EffectService.DefaultStackCount;

 /// <summary>
 /// Application level used by the effect table's stat modifier: <c>Basic_Add_Value + Level_Multiply_Value * level</c>.
 /// It is the level of the granting skill (for item buffs, the item's skill row; for skill casts, the cast level),
 /// and 1 when there is no level source. Not part of the 2492 wire layout.
 /// </summary>
    public byte EffectLevel { get; init; } = EffectService.DefaultEffectLevel;

 /// <summary>Handle of the actor that applied the effect (the player itself for item use).</summary>
    public uint CasterHandle { get; init; }

 /// <summary>Remaining duration in milliseconds when applied. 0 means permanent.</summary>
    public ulong DurationMs { get; init; }

 /// <summary>Absolute expiry time (UTC), or <c>null</c> for a permanent effect.</summary>
    public DateTime? ExpiresAt { get; init; }

    // Target and periodic (damage-over-time / heal-over-time) fields. They are additive: a plain timed buff leaves
    // them at their defaults (TickIntervalMs == 0, HpPerTick == SpPerTick == 0), so it is not periodic. It is added
    // with 2492 and removed on expiry with 2493/2494, and nothing ticks. A periodic effect sets a non-zero
    // TickIntervalMs and a signed per-tick delta; the effect service then applies that delta to the target's HP/SP
    // and announces each tick with 2495 / 2496.

 /// <summary>
 /// Handle of the actor the effect is applied to. For a self-cast item buff this is the player's own entity ID;
 /// for a skill or damage-over-time effect it is the struck target (actor handles are type-tagged in the low
 /// byte: 0x11 for a player, 0x13 for an NPC). Written at record offset +8 of 2495 and the subject of 2494 on removal.
 /// </summary>
    public uint TargetHandle { get; init; }

 /// <summary>
 /// <c>true</c> when <see cref="TargetHandle"/> refers to an NPC rather than a player. NPC effects are tracked by the
 /// effect service, not by the NPC state.
 /// </summary>
    public bool TargetIsNpc { get; init; }

 /// <summary>Map the target was on when the effect was applied.</summary>
    public uint MapId { get; init; }

 /// <summary>
 /// Caster's character name, used for NPC threat attribution on damage ticks. Empty for non-player casters.
 /// </summary>
    public string? CasterName { get; init; }

 /// <summary>
 /// Signed HP change applied to the target on each tick. Negative is damage and positive is healing. 0 means the
 /// effect does not change HP. The magnitude is supplied by the caller.
 /// </summary>
    public int HpPerTick { get; init; }

 /// <summary>Signed SP change applied to the target on each tick. 0 means the effect does not change SP.</summary>
    public int SpPerTick { get; init; }

 /// <summary>Tick period in milliseconds. 0 means the effect is not periodic and only expires.</summary>
    public int TickIntervalMs { get; init; }

 /// <summary>When the next periodic tick is due (UTC). Advanced by <see cref="TickIntervalMs"/> after each tick.</summary>
    public DateTime NextTickAt { get; set; }

 /// <summary>
 /// Whether the effect changes HP or SP on a timer (a damage or heal over time) rather than being a plain timed buff.
 /// </summary>
    public bool IsPeriodic => TickIntervalMs > 0 && (HpPerTick != 0 || SpPerTick != 0);
}
