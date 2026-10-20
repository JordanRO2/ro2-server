using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1375 ReqQuestGiveUp: abandons a quest.</summary>
/// <remarks>
/// Removes the quest from the character's state and database and returns the items its accept handed out.
/// A quest the character does not hold is refused with result 21711.
/// </remarks>
public class QuestGiveUpHandler : ZoneRmiHandlerBase<QuestGiveUpHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqQuestGiveUp;
    public override string Name => "QuestGiveUp";

    public QuestGiveUpHandler(IServiceScopeFactory s, ILogger<QuestGiveUpHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        
        
        
        
        
        if (payload.Length < 14) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var questId = reader.ReadUInt32();

        
        
        var wasActive = player.ActiveQuests.Remove(questId);
        player.QuestKillProgress.Remove(questId);
        
        
        
        LootService.ResetExtraKillProgress(player, questId);
        QuestUseItemProgress.Reset(player, questId);

        
        
        
        
        
        
        
        
        if (!wasActive)
        {
            Logger.LogInformation("{Name} tried to give up quest {QuestId}, which is not active: refused with {Code}",
                player.Name, questId, QuestProgressPackets.QuestErrorPrizeRejected);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestGiveup,
                writer => writer.WriteBytes(BuildQuestGiveupRefused2420(questId)));
            return;
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (wasActive
            && services.GetRequiredService<GameDataStore>().Quests.TryGetValue(questId, out var quest))
        {
            var grants = QuestAcceptHandler.AcceptGrants(quest);
            if (grants.Count > 0)
            {
                var inventory = services.GetRequiredService<IInventoryRepository>();
                var bag = await inventory.GetByCharacterAsync(player.CharacterId);
                foreach (var (itemId, count) in grants)
                    await QuestPrizeHandler.RemoveFromBagAsync(session, player, inventory, bag, itemId, count,
                        Logger, services);
            }
        }

        
        var questRepo = services.GetRequiredService<IQuestRepository>();
        await questRepo.GiveUpQuestAsync(player.CharacterId, questId);

        Logger.LogInformation("{Name} gave up quest {QuestId}", player.Name, questId);

        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnQuestGiveup,
            writer => writer.WriteBytes(QuestProgressPackets.BuildQuestGiveup2420(questId)));
    }

 /// <summary>Builds the refusal form of the 2420 OnQuestGiveup message (result 21711).</summary>
    private static byte[] BuildQuestGiveupRefused2420(uint questId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(QuestProgressPackets.MsgTagQuestGiveup);        
            p.WriteUInt32(0);                                             
            p.WriteUInt32(questId);                                       
            p.WriteUInt32(QuestProgressPackets.QuestErrorPrizeRejected);  
            p.WriteByte(0);                                               
        });
        return pw.ToArray();
    }
}
