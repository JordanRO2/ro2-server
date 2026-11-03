using RO2.GameData.Tables;
using RO2.Kharas.Domain;

namespace RO2.Kharas.Application;

/// <summary>
/// The 1438 ReqKharaTitleSelect decision: whether the select is accepted, and the state and answer slots it yields. Decide-only
/// (nothing is stored or published here). The stored title state and the character's play instant are the host's (its clock and
/// cache), so they come in as arguments; the one read made here is the character's completed Kharas.
/// </summary>
public sealed class SelectKharaTitleUseCase
{
    private readonly IKharaStateRepository _kharaState;

    /// <summary>Creates the use case over the per-character Khara state port.</summary>
    public SelectKharaTitleUseCase(IKharaStateRepository kharaState) => _kharaState = kharaState;

    /// <summary>
    /// Decides the select. <paramref name="state"/> is the stored title state and <paramref name="playNow"/> the character's play
    /// instant (both read by the host); <paramref name="kharaInfo"/> are the Khara_Info rows the title-granting Kharas come from.
    /// </summary>
    public async Task<KharaTitleDecision> ExecuteAsync(uint characterId, KharaTitleState state, uint titleIdA, uint titleIdB,
        DateTime playNow, IReadOnlyList<KharaInfo> kharaInfo, CancellationToken ct)
    {
        var khara = await _kharaState.GetAsync(characterId, ct);
        var completed = (IReadOnlyCollection<uint>?)khara?.Completed ?? Array.Empty<uint>();
        var titleKharas = KharaTitleRules.TitleGrantingKharas(kharaInfo);
        return KharaTitleRules.Decide(state, titleIdA, titleIdB, playNow, id => KharaTitleRules.MayWear(id, completed, titleKharas));
    }
}
