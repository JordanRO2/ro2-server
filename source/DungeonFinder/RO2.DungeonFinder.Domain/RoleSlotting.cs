using RO2.GameData.Tables;

namespace RO2.DungeonFinder.Domain;

/// <summary>The finder's role-slotting priority, shared by the queue and the party round.</summary>
public static class RoleSlotting
{
 /// <summary>The first of <paramref name="flags"/>' roles, in tank, healer, dealer order, with a free slot in the list's quota; 0 for none.</summary>
    public static byte FirstFreeRole(FindDungeonList row, RoleCounts filled, byte flags)
    {
        if ((flags & RoleKind.RoleTank) != 0 && filled.Tanks < row.Role_Tanker) return RoleKind.RoleTank;
        if ((flags & RoleKind.RoleHealer) != 0 && filled.Healers < row.Role_Healer) return RoleKind.RoleHealer;
        if ((flags & RoleKind.RoleDealer) != 0 && filled.Dealers < row.Role_Dealer) return RoleKind.RoleDealer;
        return 0;
    }
}
