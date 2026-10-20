namespace RO2.Domain.Entities;

/// <summary>One craft offer of a private shop: a Craft_ItemList recipe id and the price the buyer pays per run.</summary>
public sealed record PrivateShopCraftOffer(ushort CraftId, ulong Price);
