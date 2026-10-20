using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1379 ReqPartyDiceStart — the looter asks the party to roll for an item of a corpse.</summary>
/// <remarks>
/// <para>
/// Body = compact ByteArray, blob <c>[u16 20][u16 58255][u32 own handle][u64 item uid @8][u32 corpse NPC handle
/// @16]</c> (body 22; <see cref="PartyDicePackets.TryParseDiceStart1379"/>). The client
/// sends it from UILootingWnd (OnMessage and LootAllItems) instead of a 1322 when its own check says the party
/// rolls for the item: it checks that the party loot mode is 2 and compares the item quality, then calls the 1379 builder.
/// </para>
/// <para>
/// Retail's answer is 2468 OnLootingBoxRouletteOpen <c>[u32 0][u64 that item uid][u32 its ItemInfo id][u8 count]</c>
/// (each echoing its request's uid), and the roll is then settled by 1380 → 2469
/// (PartyDiceJoinHandler). It never answers with 2788 NfyPartyDiceChattingCmd, which an older version of this
/// handler broadcast with a random value.
/// </para>
/// <para>
/// Answered only for an item still on a corpse the sender may loot (GroundItemService, filled by the kill) and
/// not already rolling, from a sender in a party; otherwise nothing is sent. Retail has no refusal reply for 1379.
/// </para>
/// </remarks>
public class PartyDiceStartHandler : ZoneRmiHandlerBase<PartyDiceStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyDiceStart;
    public override string Name => "PartyDiceStart";

    public PartyDiceStartHandler(IServiceScopeFactory s, ILogger<PartyDiceStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!PartyDicePackets.TryParseDiceStart1379(payload.Span, out var itemUid, out var corpseHandle))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyDiceStart ({Len}B)", session.SessionId, payload.Length);
            return;
        }

        var npc = services.GetRequiredService<NpcStateManager>().GetNpc(corpseHandle);
        DateTime? deathStamp = npc is { IsDead: true } ? npc.DeathTime : null;
        if (!GroundItemService.TryGetCorpseLoot(player.MapId, corpseHandle, player.CharacterId, deathStamp, out var loot)
            || loot.Items.FirstOrDefault(i => i.Uid == itemUid) is not { } item)
        {
            Logger.LogDebug("{Name} ReqPartyDiceStart item {Uid:X} corpse 0x{Corpse:X8}: not on a corpse they may loot, not answered",
                player.Name, itemUid, corpseHandle);
            return;
        }

        var party = services.GetRequiredService<PartyService>().GetPlayerParty(player.SessionId);
        if (party == null || PartyDiceService.IsRolling(item.Uid))
        {
            Logger.LogDebug("{Name} ReqPartyDiceStart item {Uid:X}: {Why}, not answered",
                player.Name, itemUid, party == null ? "not in a party" : "already rolling");
            return;
        }

        await PartyDiceService.OpenAsync(PartyDiceService.Deps.From(services, Logger), player, party, loot, item);
    }
}
