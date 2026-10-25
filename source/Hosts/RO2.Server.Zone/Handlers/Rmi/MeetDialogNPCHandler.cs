using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1698 (NfyMeetDialogNPC): the client opened a dialog NPC's window. Advances the talk objectives of
/// multi-talk quests for that NPC.
/// </summary>
/// <remarks>
/// <para>
/// Body: a bare u32 NPC handle (4 bytes, no envelope). The dialogue window itself is opened client-side.
/// </para>
/// <para>
/// Reply: 2432 OnQuestUpdateNotify for every held quest with two or more talk rows naming the met NPC, sent on
/// each meeting. When the meeting leaves every such row met, a 2427 OnQuestComplete is sent for the quest. The
/// 2427 is sent only while <c>QuestKillProgress</c> is 0, and sets it to 1.
/// </para>
/// <para>
/// A single-row talk quest whose talk NPC is not its reward NPC is closed the same way: one meeting meets the
/// tracked row, so 2432 and 2427 are sent together. Each meeting and close is stored with the quest, so it
/// survives a relog.
/// </para>
/// </remarks>
public class MeetDialogNPCHandler : ZoneRmiHandlerBase<MeetDialogNPCHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.NfyMeetDialogNPC;
    public override string Name => "MeetDialogNPC";

    public MeetDialogNPCHandler(IServiceScopeFactory s, ILogger<MeetDialogNPCHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4) return;
        var npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        var npc = services.GetRequiredService<NpcStateManager>().GetNpc(npcHandle);

        Logger.LogDebug("Session {Id}: {Name} meets dialog NPC {Handle:X8} (asset {Asset}); the client opens the window",
            session.SessionId, player.Name, npcHandle, npc?.NpcId ?? 0);
        
        
        
        if (npc is null || npc.NpcId == 0) return;

        var store = services.GetRequiredService<GameDataStore>();
        QuestTalkProgress.Purge(player);

        
        foreach (var questId in player.ActiveQuests.ToArray())
        {
            if (!store.QuestRows.TryGetValue(questId, out var rows)) continue;
            if (QuestTalkProgress.Meet(player, questId, rows, npc.NpcId) is not { } meeting) continue;

            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestUpdateNotify,
                writer => writer.WriteBytes(QuestNotifyPackets.BuildQuestUpdateNotify2432(questId, npc.NpcId)), ct);

            player.QuestKillProgress.TryGetValue(questId, out var progress);
            if (!meeting.AllMet || progress != 0)
            {
                Logger.LogDebug("{Name} quest {QuestId}: talk target {Npc} met", player.Name, questId, npc.NpcId);
                
                
                if (progress == 0)
                    await QuestProgressStore.SaveObjectiveStateAsync(services.GetRequiredService<IQuestRepository>(),
                        player, questId, store, Logger);
                continue;
            }

            
            player.QuestKillProgress[questId] = 1;
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestComplete2427(questId)), ct);
            Logger.LogInformation("{Name} quest {QuestId}: every talk target met, objective closed", player.Name, questId);
            
            
            await QuestProgressStore.SaveObjectiveStateAsync(services.GetRequiredService<IQuestRepository>(),
                player, questId, store, Logger);

            
            
            
            
            if (QuestAcceptHandler.ObjectiveCloseGrants(questId).Count > 0)
                await QuestPrizeHandler.GrantObjectiveCloseItemsAsync(session, player, questId, store,
                    services.GetRequiredService<IInventoryRepository>(), Logger);
        }
    }
}
