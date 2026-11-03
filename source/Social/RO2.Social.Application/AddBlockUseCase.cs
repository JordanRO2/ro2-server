using RO2.Socials.Domain;

namespace RO2.Socials.Application;

/// <summary>
/// What an add-block decided. <see cref="RefusalCode"/> is the 23510 refusal result when there is one (7 already
/// registered); it is null when the character was added (the host sends the success record) and for
/// <see cref="BlockAddOutcome.ListFull"/>, which the protocol has no answer for (the host drops the request).
/// </summary>
public readonly record struct AddBlockResult(BlockAddOutcome Outcome, ushort? RefusalCode)
{
    /// <summary>Whether the character was added.</summary>
    public bool Added => Outcome == BlockAddOutcome.Added;
}

/// <summary>
/// The add-block decision from the write onward: the conditional add and its mapping to the 23510 result. The host keeps
/// the throttle, parse, name lookup, self-block drop and the wire.
/// </summary>
public sealed class AddBlockUseCase
{
    private readonly IBlockListRepository _blocks;

    /// <summary>Creates the use case over the block list port.</summary>
    public AddBlockUseCase(IBlockListRepository blocks) => _blocks = blocks;

    /// <summary>Adds <paramref name="targetId"/> (already resolved by the host) to the owner's block list.</summary>
    public async Task<AddBlockResult> ExecuteAsync(uint ownerId, uint targetId, string targetName, CancellationToken ct = default)
    {
        var outcome = await _blocks.AddAsync(ownerId, targetId, targetName, ct);
        return new AddBlockResult(outcome,
            outcome == BlockAddOutcome.AlreadyRegistered ? SocialRules.AddBlockResultAlreadyRegistered : null);
    }
}
