
namespace RO2.Economy.Domain;

/// <summary>
/// Persistence for one character's private-shop craft offers (1476 ReqAddPrivateShopCraft, withdrawn by 1477),
/// kept as an ordered list so a seller's 1473 lists them again in a later session, as retail's does. Raw
/// storage only: the zone-session load gate, the per-seller write ordering and the add/remove rules live in
/// the Zone, which keeps its PrivateShopRegistry authoritative and saves whole snapshots through this interface.
/// </summary>
public interface IPrivateShopCraftRepository
{
 /// <summary>The character's stored offers in list order (empty when it has none).</summary>
    Task<IReadOnlyList<PrivateShopCraftOffer>> GetOffersAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Stores the character's whole ordered list, replacing what was there (upsert).</summary>
    Task ReplaceOffersAsync(uint characterId, IReadOnlyList<PrivateShopCraftOffer> offers, CancellationToken ct = default);
}
