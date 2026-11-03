namespace RO2.PartyDice.Domain;

/// <summary>What a dice round wants done, in order. The host turns each effect into its frame or corpse change.</summary>
public abstract record DiceEffect;

/// <summary>The roll opened: tell each of these sessions, in order, to open the roll window (2468).</summary>
public sealed record Opened(IReadOnlyList<uint> ToSessionIds) : DiceEffect;

/// <summary>A participant rolled: announce the roll to the party (2469).</summary>
public sealed record Answered(ulong Gid, ushort Roll) : DiceEffect;

/// <summary>A participant passed: announce it to the party (2470).</summary>
public sealed record Refused(ulong Gid) : DiceEffect;

/// <summary>Nobody rolled: announce the all-pass notice to the party (2469 with the all-passed result).</summary>
public sealed record AllPassed : DiceEffect;

/// <summary>The highest roll won: award the item to this participant.</summary>
public sealed record Winner(uint SessionId, ulong Gid, ushort Roll) : DiceEffect;

/// <summary>After an all-pass: the item loses its dice mark and the looter can take it normally.</summary>
public sealed record ReleaseDiceBound : DiceEffect;

/// <summary>
/// After a winner: the item keeps its dice mark. A successful award takes the item off the corpse; a winner whose
/// award does not land (full bag, item gone) leaves it on the corpse, still marked for dice.
/// </summary>
public sealed record HoldDiceBound : DiceEffect;
