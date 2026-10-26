using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// Where a player's <see cref="FuncObjectControlSlot"/> lives. The host implements it over its live player registry, so a
/// slot is released with its player; the player type is the host's.
/// </summary>
/// <typeparam name="TPlayer">The host's player type.</typeparam>
public interface IFuncObjectControlSlots<in TPlayer> where TPlayer : class
{
    /// <summary>The player's slot, created on first use.</summary>
    FuncObjectControlSlot Slot(TPlayer player);

    /// <summary>The player's slot, or null when the player never began a control.</summary>
    FuncObjectControlSlot? Find(TPlayer player);
}
