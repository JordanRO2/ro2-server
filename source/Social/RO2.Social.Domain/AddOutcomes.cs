namespace RO2.Socials.Domain;

/// <summary>Outcome of adding a friend: the three cases the 23508 result codes distinguish.</summary>
public enum AddOutcome
{
    /// <summary>The friend was added (23508 result 0).</summary>
    Added,
    /// <summary>The friend was already on the list, nothing written (23508 result 6).</summary>
    AlreadyFriend,
    /// <summary>The list was at the cap, nothing written (23508 result 33).</summary>
    ListFull,
}

/// <summary>Outcome of adding a block-list entry.</summary>
public enum BlockAddOutcome
{
    /// <summary>The character was added to the list.</summary>
    Added,
    /// <summary>The character was already on the list, nothing written (23510 result 7).</summary>
    AlreadyRegistered,
    /// <summary>The list was at its defensive cap, nothing written (the protocol has no answer for this case).</summary>
    ListFull,
}
