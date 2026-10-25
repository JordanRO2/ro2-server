namespace RO2.Characters.Domain;

/// <summary>One trait contribution read off an item or title: its kind, stat type, and raw value.</summary>
/// <param name="Kind">Trait_Kind: 1 = flat amount, 2 = percent (stored ×100), 3 = effect line.</param>
/// <param name="Type">Trait_Type: the 2498 stat id the contribution targets.</param>
/// <param name="Value">The raw Value from the trait row.</param>
public readonly record struct TraitLine(byte Kind, byte Type, float Value);
