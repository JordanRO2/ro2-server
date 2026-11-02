using System.Reflection;
using System.Text.Json;
using RO2.GameData;
using RO2.Inventory.Domain;

namespace RO2.Tests.QuestCharacterization;

/// <summary>
/// Builders shared by the B8 S1 quest characterization tests. The tests pin what the host code does TODAY (bugs and quirks
/// included) so that a byte-identical move of a rule into RO2.Quest.Domain keeps them green. Quest ids used here are
/// synthetic, so the shipped data/ tables, which several quest services read lazily from the working directory, never
/// match them.
/// </summary>
internal static class Q
{
    /// <summary>A Quest_Info row: objective type and its three values, plus the reward NPC.</summary>
    public static QuestData Row(byte type, uint v1 = 0, uint v2 = 0, uint v3 = 0, uint questId = 990000001, uint rewardNpc = 0)
        => new() { Quest_ID = questId, Complete_Type = type, Complete_Type_Value1 = v1, Complete_Type_Value2 = v2, Complete_Type_Value3 = v3, Reward_NPC_ID = rewardNpc };

    /// <summary>A GameDataStore holding only the given Quest_Info rows (rows of one quest keep the given order).</summary>
    public static async Task<GameDataStore> StoreAsync(params QuestData[] rows)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-b8s1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Quest_Info.json"), JsonSerializer.Serialize(rows));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A main-bag stack (StorageType 0) of an item.</summary>
    public static InventoryItem Stack(uint itemId, int count, byte storageType = 0)
        => new() { Id = (ulong)(itemId * 31 + (uint)count), CharacterId = 1, ItemId = itemId, Count = count, StorageType = storageType };

    /// <summary>The value of a private/internal constant, read by reflection (the carve must keep the name).</summary>
    public static object? PrivateConst(Type type, string name)
        => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetRawConstantValue();

    /// <summary>Calls a private static method by reflection (the carve must keep the name).</summary>
    public static object? CallPrivateStatic(Type type, string name, params object?[] args)
        => type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, args);
}
