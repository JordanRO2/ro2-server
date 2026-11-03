namespace RO2.Rankings.Application;

/// <summary>What a board row shows about a ranked character.</summary>
public readonly record struct CharacterRankingInfo(string Name, byte ClassId, byte Profession);

/// <summary>Outbound port: the characters a roll ranks. Owned by the Characters side, implemented by the host.</summary>
public interface ICharacterRankingLookup
{
 /// <summary>The non-deleted characters among <paramref name="charIds"/>, keyed by character id.</summary>
    Task<IReadOnlyDictionary<uint, CharacterRankingInfo>> LookupAsync(IReadOnlyCollection<uint> charIds,
        CancellationToken ct);
}
