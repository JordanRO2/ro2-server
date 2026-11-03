using RO2.GameData.Tables;

namespace RO2.Collections.Domain;

/// <summary>What using a hatched egg on a pet does.</summary>
public enum PetEggKind
{
    /// <summary>The pet is not in the book yet: it is registered at loyalty 0 and the egg is taken.</summary>
    Registered,
    /// <summary>The pet is in the book and below its loyalty cap: its loyalty rises and the egg is taken.</summary>
    Fed,
    /// <summary>The pet is already at its loyalty cap: nothing changes and no egg is taken.</summary>
    Capped,
}

/// <summary>The outcome of using an egg: what happened, the pet loyalty afterwards, and the 2925 flag and result.</summary>
public readonly record struct PetEggOutcome(PetEggKind Kind, ushort Loyalty, byte Flag, ushort Result);

/// <summary>
/// The pure decision of RMI 1627 (use a hatched pet egg): register the pet, feed it, or refuse at the loyalty cap. The pet row comes in as an
/// argument; the egg lookup, the book, the bag and the wire stay in the Zone host.
/// </summary>
public static class PetEggDecision
{
    /// <summary>2925 flag of a refusal: no egg taken, the result says why.</summary>
    public const byte FlagRefused = 0;

    /// <summary>2925 flag for a pet the egg added to the book.</summary>
    public const byte FlagAdded = 1;

    /// <summary>2925 flag for an egg fed to a pet already in the book.</summary>
    public const byte FlagFed = 2;

    /// <summary>2925 result: the fed pet is already at its loyalty cap, so no egg is taken.</summary>
    public const ushort ResultLoyaltyCapped = 30001;

    /// <summary>2925 result: the named cell no longer holds the hatched egg of the pet.</summary>
    public const ushort ResultNoEgg = 30004;

    /// <summary>Decides one egg use on <paramref name="pet"/>, given whether it is in the book and its stored loyalty.</summary>
    public static PetEggOutcome Decide(PetInfo pet, bool registered, ushort loyalty)
    {
        if (!registered)
            return new(PetEggKind.Registered, 0, FlagAdded, 0);
        return CollectionRules.FedLoyalty(pet, loyalty) is { } fed
            ? new(PetEggKind.Fed, fed, FlagFed, 0)
            : new(PetEggKind.Capped, loyalty, FlagRefused, ResultLoyaltyCapped);
    }
}
