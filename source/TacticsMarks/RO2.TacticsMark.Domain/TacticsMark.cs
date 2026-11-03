namespace RO2.TacticsMarks.Domain;

/// <summary>One mark on the party's list: the mark id, the target it sits on and when it was set.</summary>
public readonly record struct TacticsMark(ushort Mark, uint Handle, DateTime SetAtUtc);
