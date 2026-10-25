namespace RO2.Inventory.Domain;

/// <summary>One bag stack as the placement planners see it: its uid, template, count and cell. Bag-only — the host
/// filters out worn and banked rows before mapping, so the planners never see them.</summary>
public readonly record struct BagStack(ulong Id, uint ItemId, int Count, short Slot);

/// <summary>Where an add lands: either <see cref="RestackId"/> (the whole quantity folds into one existing stack) or
/// <see cref="NewStacks"/> (the (cell, count) of each new stack to create).</summary>
public sealed record PlacementPlan(ulong? RestackId, IReadOnlyList<(short Cell, int Count)> NewStacks);

/// <summary>One stack a take draws from, and the count it is left with (0 when emptied).</summary>
public readonly record struct StackTake(ulong StackId, int Left);

/// <summary>
/// One entry's placement in a <see cref="BagStackPlanner.PlanSequence"/> run. Exactly one outcome holds:
/// <see cref="Refused"/> (no room, no cells consumed); a restack, when <see cref="MergeIntoSlot"/> is set, into the
/// stack at that cell (an existing stack or one opened by an earlier entry), leaving it with <see cref="MergedCount"/>;
/// or <see cref="Opened"/> new stacks at the given cells and counts. A skipped entry (no item / non-positive quantity)
/// is a null element in the result, not a <see cref="SequencedPlacement"/>.
/// </summary>
/// <param name="Refused">The entry found no room (fewer free cells left than it needs); no cell was consumed.</param>
/// <param name="MergeIntoSlot">The cell of the stack the whole quantity folds into, or null when it opens new stacks.</param>
/// <param name="MergedCount">With <see cref="MergeIntoSlot"/>: the stack's count after the merge.</param>
/// <param name="Opened">The new stacks this entry opens, each a (cell, count), in cell order.</param>
public sealed record SequencedPlacement(bool Refused, short? MergeIntoSlot, int MergedCount,
    IReadOnlyList<(short Cell, int Count)> Opened);
