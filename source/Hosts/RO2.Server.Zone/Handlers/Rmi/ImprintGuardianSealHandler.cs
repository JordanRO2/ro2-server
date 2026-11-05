using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Characters.Domain;
using RO2.GameData;
using RO2.Inventory.Domain;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Configuration;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles ReqImprintGuardianSeal (1694): imprints one rolled seal line on an accessory.</summary>
/// <remarks>
/// <para>
/// The request body is <c>[u32 tag][u64 TARGET equipment uid][u64 SOURCE seal-item uid]</c>, 20 bytes (capture-locked).
/// The seal APPLIES here; the 3031 answer only tells the client to refresh the item.
/// </para>
/// <para>
/// Refused (3031 with a non-zero result, nothing charged or consumed) when: either item is not the player's, the source
/// is not a SealItem_Info row, the target's Equip_Type has no SealSlot_Info slot, the group has no configured roll
/// candidates (no invented balance), or the wallet is short. Otherwise the Need_Gold fee is debited, one seal item is
/// consumed, one line is rolled with <see cref="SealRoll"/> and stored on <see cref="InventoryItem.SealJson"/>.
/// </para>
/// </remarks>
public class ImprintGuardianSealHandler : ZoneRmiHandlerBase<ImprintGuardianSealHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqImprintGuardianSeal;
    public override string Name => "ImprintGuardianSeal";
    public ImprintGuardianSealHandler(IServiceScopeFactory s, ILogger<ImprintGuardianSealHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Result 0 = imprinted.</summary>
    private const ushort ResultOk = 0;
    /// <summary>Result 1 = refused (the exact retail refusal code is not captured).</summary>
    private const ushort ResultRefused = 1;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 20)
        {
            Logger.LogWarning("{Name}: short 1694 body ({Len} B, retail is 20)", player.Name, payload.Length);
            return;
        }
        var reader = new PacketReader(payload.Span);
        reader.ReadUInt32();                       // tag, ignored
        ulong targetUid = reader.ReadUInt64();
        ulong sourceUid = reader.ReadUInt64();

        Task Answer(ushort result, uint value) => session.SendRmiAsync(ZoneS2COpcodes.AnsImprintGuardianSeal, w =>
        {
            w.WriteUInt16(result);
            w.WriteUInt16(1);
            w.WriteByte(0);
            w.WriteUInt32(value);
        });

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var target = await inventory.GetByIdAsync(targetUid);
        var source = await inventory.GetByIdAsync(sourceUid);

        var sealRow = source is null ? null : store.SealItemInfo.FirstOrDefault(r => r.ItemID == source.ItemId);
        ushort equipType = target is null ? (ushort)0 : store.GetItem(target.ItemId)?.Equip_Type ?? 0;
        // Equip_Type 0 appears in SealSlot_Info (Item_Type 127 row) but is "not equippable", so it never qualifies.
        bool valid = target is { } t && source is { } s
            && t.CharacterId == player.CharacterId && s.CharacterId == player.CharacterId
            && s.StorageType == 0 && s.Count > 0
            && sealRow is not null
            && equipType != 0 && store.SealSlotInfo.Any(r => r.Equip_Type == equipType);

        var candidates = new List<SealRoll.Candidate>();
        if (valid && services.GetRequiredService<ServerConfiguration>().Game.Seal.ReinforceRolls
                .TryGetValue(sealRow!.Seal_Reinforce_ID, out var lines))
            candidates.AddRange(lines.Select(l => new SealRoll.Candidate(l.Kind, l.TraitType, l.MinValue, l.MaxValue)));

        if (!valid || candidates.Count == 0 || !player.TryDebit(sealRow!.Need_Gold))
        {
            Logger.LogDebug("{Name} imprint refused: target {T} source {S} (valid {Valid}, candidates {N})",
                player.Name, targetUid, sourceUid, valid, candidates.Count);
            await Answer(ResultRefused, 0);
            return;
        }

        (byte bag, byte slot) = ItemWorkbench.ClientBagSlot(source!);
        if (await inventory.AdjustCountAsync(sourceUid, player.CharacterId, -1) is not { } remaining)
        {
            player.Credit(sealRow.Need_Gold);   // the seal item vanished meanwhile: nothing consumed, refund
            await Answer(ResultRefused, 0);
            return;
        }
        if (remaining <= 0) await inventory.RemoveItemAsync(sourceUid);

        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnZenyChange,
            ActorVitalsPackets.BuildZenyChange2435(player.EntityId, player.Gold));
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, w => w.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, sourceUid,
                (byte)Math.Clamp(remaining, 0, byte.MaxValue), useSource: CombatPackets.UseSourceItemUse)));

        var line = SealRoll.Roll(candidates, new Random())!.Value;   // candidates is non-empty
        target!.SealJson = SealLines.Serialize([line]);
        await inventory.UpdateItemAsync(target);

        // Re-push the 2498 sheet so a sealed WORN accessory's stats apply at once (same path as the refine handler).
        if (player.Character is { } ch)
        {
            var worn = await inventory.GetEquipmentAsync(ch.Id);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
                services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, ch, worn));
        }

        // The u32 is INFERRED (not decoded): the target's uid low word, which is what the client item-window refresh needs.
        await Answer(ResultOk, (uint)(targetUid & 0xFFFFFFFF));

        Logger.LogInformation("{Name} imprinted seal on uid {Uid}: kind {K} type {Ty} value {V}, fee {Fee}",
            player.Name, targetUid, line.Kind, line.Type, line.Value, sealRow.Need_Gold);
    }
}
