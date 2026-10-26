using System.Collections.Generic;
using System.Linq;
using RO2.Characters.Application;
using RO2.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="ICharacterGameData"/> with the host's loaded game-data tables.</summary>
internal sealed class CharacterGameData(GameDataStore store) : ICharacterGameData
{
    public bool UnionExists(uint unionId) => store.UnionInfo.Any(u => u.Union_ID == unionId);

    public IReadOnlyDictionary<byte, ExpTableEntry> ExpTable => store.ExpTable;

    public (int str, int agi, int intel, int wis, int vit, int luk)? ClassBasePrimaries(byte job)
        => StatService.ClassBasePrimaries(store, job);

    public int BonusStatCost(int currentStatValue) => StatService.BonusStatCost(store, currentStatValue);

    public (int hp, int mp, int str, int agi, int intel, int wis, int vit, int luk)? SeedStats(byte job)
    {
        if (StatService.ClassBasePrimaries(store, job) is not { } bp) return null;
        var bs = StatService.ComputeBaseStats(store, job, 1, bp.str, bp.agi, bp.intel, bp.wis, bp.vit, bp.luk);
        return bs.MaxHp > 0 ? ((int)bs.MaxHp, bs.MaxSp, bp.str, bp.agi, bp.intel, bp.wis, bp.vit, bp.luk) : null;
    }

    public bool IsAppearanceTicket(uint itemId)
        => store.GetItem(itemId)?.Item_Type == AppearanceChangePackets.AppearanceTicketItemType;

    public byte RequireSex(uint itemId) => store.GetItem(itemId)?.Require_Sex ?? 0;
}
