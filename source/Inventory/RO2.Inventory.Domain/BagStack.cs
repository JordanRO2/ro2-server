namespace RO2.Inventory.Domain;

/// <summary>One bag stack as the placement planners see it: its uid, template, count and cell. Bag-only — the host
/// filters out worn and banked rows before mapping, so the planners never see them.</summary>
public readonly record struct BagStack(ulong Id, uint ItemId, int Count, short Slot);

/// <summary>Where an add lands: either <see cref="RestackId"/> (the whole quantity folds into one existing stack) or
/// <see cref="NewStacks"/> (the (cell, count) of each new stack to create).</summary>
public sealed record PlacementPlan(ulong? RestackId, IReadOnlyList<(short Cell, int Count)> NewStacks);

/// <summary>One stack a take draws from, and the count it is left with (0 when emptied).</summary>
public readonly record struct StackTake(ulong StackId, int Left);
