namespace RO2.Worlds.Domain;

/// <summary>
/// The pure rule that decides whether a 1420 ReqChannelMove may proceed: a move to the account's current channel, or to
/// a handle this world does not advertise, is refused with a 2556 result code. The channel handles themselves (their bit
/// layout) belong to the host; the rule only compares against the handles it is given.
/// </summary>
public static class ChannelMoveRules
{
    /// <summary>2556 result code: the requested channel is the account's current channel.</summary>
    public const ushort ResultCurrentChannel = 1018;

    /// <summary>2556 result code: the requested channel does not exist.</summary>
    public const ushort ResultNoSuchChannel = 1014;

    /// <summary>
    /// The 2556 refusal code for a move to <paramref name="requested"/>, or null when the move may proceed.
    /// <paramref name="current"/> is the account's recorded channel, or null when there is none (zero counts as none).
    /// <paramref name="channelIds"/> are the handles of the channels the world advertises, walked in order until the
    /// requested one is found.
    /// </summary>
    public static ushort? RefusalCode(uint requested, uint? current, IEnumerable<uint> channelIds)
    {
        if (current is uint c && c != 0 && requested == c)
            return ResultCurrentChannel;
        foreach (var id in channelIds)
            if (id == requested)
                return null;
        return ResultNoSuchChannel;
    }
}
