using RO2.GameData.Tables;
using RO2.Kharas.Domain;

namespace RO2.Kharas.Application;

/// <summary>
/// The 1438 ReqKharaTitleSelect decision: whether the select is accepted, and the state and answer slots it yields. Decide-only
/// (nothing is stored or published here). The stored title state and the character's play instant are the host's (its clock and
/// cache), so they come in as arguments, with the character's completed Kharas.
/// </summary>
public sealed class SelectKharaTitleUseCase
{
    /// <summary>
    /// Decides the select. <paramref name="state"/> is the stored title state and <paramref name="playNow"/> the character's play
    /// instant and <paramref name="completed"/> the character's completed Kharas (all read by the host); <paramref name="kharaInfo"/> are the Khara_Info rows the title-granting Kharas come from.
    /// </summary>
    public KharaTitleDecision Execute(KharaTitleState state, uint titleIdA, uint titleIdB,
        DateTime playNow, IReadOnlyCollection<uint> completed, IReadOnlyList<KharaInfo> kharaInfo)
    {
        var titleKharas = KharaTitleRules.TitleGrantingKharas(kharaInfo);
        return KharaTitleRules.Decide(state, titleIdA, titleIdB, playNow,
            id => KharaTitleRules.MayWear(id, completed, titleKharas),
            id => KharaTitleRules.IsSecondaryBoardKhara(id, kharaInfo));
    }
}
