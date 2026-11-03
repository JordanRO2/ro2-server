using RO2.Application.Interfaces;
using RO2.Characters.Domain;
using RO2.Rankings.Application;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="ICharacterRankingLookup"/> with the scoped character repository; deleted characters are dropped.</summary>
internal sealed class CharacterRankingLookupAdapter(ICharacterRepository characters) : ICharacterRankingLookup
{
    public async Task<IReadOnlyDictionary<uint, CharacterRankingInfo>> LookupAsync(IReadOnlyCollection<uint> charIds,
        CancellationToken ct)
    {
        var found = await characters.GetByIdsAsync(charIds, ct);
        return found.Where(c => !c.IsDeleted).ToDictionary(c => c.Id, c =>
            new CharacterRankingInfo(c.Name, CharacterHeadRules.CombatJobByte(c), CharacterHeadRules.CreationProfessionByte(c)));
    }
}
