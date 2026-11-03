namespace RO2.Socials.Domain;

/// <summary>
/// The pure social-graph rules: list caps and the messenger result codes (23508 AnsAddFriend, 23506/23510/23511
/// block list). The values are wire-critical.
/// </summary>
public static class SocialRules
{
    /// <summary>
    /// Friends one character may list. No data/ table holds a friend limit, so this is a server constant: 200.
    /// The limit is checked BEFORE the name is looked up, and it is measured on the count the owner's 23505 lists.
    /// </summary>
    public const int MaxFriends = 200;

    /// <summary>
    /// A defensive ceiling on one owner's block list, so a scripted client cannot grow the document without
    /// bound (and so the 23506 count, written as a u16, can never approach its wrap point).
    /// No data table defines a block-list limit; 200 mirrors <see cref="MaxFriends"/> as a sane bound.
    /// </summary>
    public const int MaxBlocked = 200;

    /// <summary>Result code 0, success: the 23508 AnsAddFriend result and the leading u16 of 23506/23510/23511.</summary>
    public const ushort ResultOk = 0;

    /// <summary>23508 result 6: the name is already on the requester's list.</summary>
    public const ushort AddResultAlreadyFriend = 6;

    /// <summary>23508 result 18: the typed name resolves to no character.</summary>
    public const ushort AddResultNotFound = 18;

    /// <summary>23508 result 33: the REQUESTER's list is full (string_ui 2286 "You can't add anymore friends.").</summary>
    public const ushort AddResultListFull = 33;

    /// <summary>23510 result 7: the name is already on the owner's block list (client string 9514).</summary>
    public const ushort AddBlockResultAlreadyRegistered = 7;

    /// <summary>23510 result 18: the typed name resolves to no character (client string_ui 9574).</summary>
    public const ushort AddBlockResultNotFound = 18;

    /// <summary>True when a friend list of <paramref name="count"/> friends takes no more.</summary>
    public static bool IsFriendListFull(int count) => count >= MaxFriends;

    /// <summary>
    /// Whether an owner still receives a presence notice (23502/23503) about a character: not when the owner's
    /// block list holds it. <paramref name="blockers"/> is the set of owner ids whose block list holds the character.
    /// </summary>
    public static bool ReceivesPresenceNotice(uint ownerId, IReadOnlySet<uint> blockers) => !blockers.Contains(ownerId);
}
