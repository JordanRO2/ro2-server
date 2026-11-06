using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>
/// Decides and applies a guild notice change (1428): a sender without a guild gets 7, a notice over the limit gets 15,
/// and an accepted notice is written once through the ledger. It sends no wire; the host reads the request, sends the
/// 2563 and then, on success, the 2564 broadcast.
/// </summary>
public sealed class GuildNoticeUseCase(IGuildNoticeLedger ledger)
{
    /// <summary>Result 0: the notice was set.</summary>
    public const ushort ResultOk = 0;

    /// <summary>Result 7: the sender is not in a guild.</summary>
    public const ushort ResultNoGuild = 7;

    /// <summary>Result 15: the notice is too long.</summary>
    public const ushort ResultTooLong = 15;

    /// <summary>Maximum notice length in UTF-16 characters, colour code included.</summary>
    public const int MaxNoticeCount = 100;

    /// <summary>The result code for a notice of <paramref name="count"/> characters (colour code included), given whether the sender is in a guild.</summary>
    public static ushort Validate(bool inGuild, int count)
        => !inGuild ? ResultNoGuild : count > MaxNoticeCount ? ResultTooLong : ResultOk;

    /// <summary>Validates and, on <see cref="ResultOk"/>, sets <paramref name="notice"/> on <paramref name="guild"/> and writes the guild once.</summary>
    public async Task<ushort> SetAsync(Guild? guild, int count, string notice, ushort colorCode = 0)
    {
        var result = Validate(guild is not null, count);
        if (result == ResultOk) await ledger.SetNoticeAsync(guild!, notice, colorCode);
        return result;
    }
}
