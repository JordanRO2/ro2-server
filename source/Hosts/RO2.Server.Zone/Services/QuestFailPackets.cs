using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2434 OnQuestFailed: the server tells the client that a quest it holds has failed.
/// </summary>
/// <remarks>
/// <b>Layout</b>: S2C, with no length prefix and no message tag, 11 bytes:
/// <c>[u32 questId][u16 code][u8 Fail_Type][u32 Fail_Type_Value1]</c>.
/// The client ends the quest, shows its fail text and a notification. For time-limit failures it also stops the quest's
/// timer. The client never fails a quest on its own, so the server must send this message. The u16 code is not read
/// by the client and is written as <see cref="FailedCode"/>. The tail is the failing row's Fail_Type_Value1.
/// </remarks>
internal static class QuestFailPackets
{
 /// <summary>Quest_Info Fail_Type of a time limit (Fail_Type_Value1 = ms).</summary>
    internal const byte FailTypeTimeLimit = 38;

 /// <summary>The 2434 u16, which the client does not read. Written as <see cref="FailedCode"/>.</summary>
    internal const ushort FailedCode = 21714;

 /// <summary>2434 body: <c>[u32 questId][u16 code][u8 failType][u32 failValue]</c>, 11 bytes.</summary>
    internal static byte[] BuildQuestFailed2434(uint questId, ushort code, byte failType, uint failValue)
    {
        var body = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(body, questId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), code);
        body[6] = failType;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), failValue);
        return body;
    }
}

/// <summary>
/// The quests that failed (2434 sent) during this login, so that the 1378 turn-in refuses them. Attached to the
/// <see cref="PlayerState"/>, so it lives for one login, like the fail timer that fills it
/// (QuestAcceptHandler). A new accept of the quest clears its mark. The failed quest stays in
/// <see cref="PlayerState.ActiveQuests"/>, so a 1375 give-up of it still works.
/// </summary>
/// <remarks>
/// Not persisted: after a relog the quest is loaded as in progress again, because the quest store has no failed state.
/// </remarks>
internal static class QuestFailState
{
    private static readonly ConditionalWeakTable<PlayerState, ConcurrentDictionary<uint, byte>> Failed = new();

    internal static void MarkFailed(PlayerState player, uint questId)
        => Failed.GetValue(player, _ => new ConcurrentDictionary<uint, byte>()).TryAdd(questId, 0);

    internal static bool IsFailed(PlayerState player, uint questId)
        => Failed.TryGetValue(player, out var set) && set.ContainsKey(questId);

    internal static void Clear(PlayerState player, uint questId)
    {
        if (Failed.TryGetValue(player, out var set)) set.TryRemove(questId, out _);
    }
}
