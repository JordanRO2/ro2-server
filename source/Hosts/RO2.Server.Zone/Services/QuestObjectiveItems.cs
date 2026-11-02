using RO2.GameData;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>Pure item-count rules of collect and deliver quest objectives, shared by the quest handlers and services.</summary>
internal static class QuestObjectiveItems
{
    private const byte StorageBag = 0;

 /// <summary>Returns how many objective items a collect or deliver turn-in takes.</summary>
    internal static int ObjectiveItemCount(QuestData quest) => (int)Math.Max(1u, quest.Complete_Type_Value2);

 /// <summary>Returns how many of <paramref name="itemId"/> the bag holds. A stack without a count counts as one item.</summary>
    internal static long CountInBag(IReadOnlyList<InventoryItem> bag, uint itemId) =>
        bag.Where(i => i.StorageType == StorageBag && i.ItemId == itemId).Sum(i => (long)Math.Max(1, i.Count));
}
