using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;
using RO2.Characters.Application;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1493 ReqUseBonusStatPoint (ByteArray) — allocate bonus stat points onto the primaries.
/// </summary>
/// <remarks>
/// Server-authoritative and ALL-OR-NOTHING, like retail. The whole request is priced first against the
/// character's unspent pool (<c>BonusStatPointsEarned(level) − sum(Bonus*)</c>, per-point cost from
/// <c>Status_Point</c>); only if all of it fits is anything applied. An accepted request is SAVED first and only
/// then mirrored to the runtime and re-pushed as 2595/2498; a failed save refuses it (BadState) with nothing
/// changed and no 2498. The 2680 ack always goes out and carries the result code the client turns into its error line.
/// </remarks>
public class UseBonusStatPointHandler : ZoneRmiHandlerBase<UseBonusStatPointHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqUseBonusStatPoint;
    public override string Name => "UseBonusStatPoint";

    public UseBonusStatPointHandler(IServiceScopeFactory s, ILogger<UseBonusStatPointHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // The body is a VECTOR of allocations (the client batches all of a "Set" into one 1493), not a
        // single point. Layout: after the compact marker+len,
        // blob+11 = entry count (u8), then entries at blob+12 stride 3 = [u8 statId][u16 amount LE].
        //   1,1,1,1,1 -> count 5: (0,1)(1,1)(2,1)(3,1)(4,1);  1,0,1,0,1 -> count 3: (0,1)(2,1)(4,1);
        //   single STR -> count 1: (0,1).  In payload coords (payload = marker+len+blob) that is
        //   count@payload+13, entry i statId@payload+14+3i, amount(u16)@payload+15+3i.
        // statId 0 lands on the 2498 STR bonus (blob+26) and 4 on the VIT bonus (blob+34): the points move
        // from the 2498 unspent slot blob+38 to the stat's bonus slot.
        var span = payload.Span;
        if (span.Length < 14) return;
        int count = span[13];

        // 2680 echoes the request's blob+11..29 (= payload+13..31) verbatim.
        var echo = span[13..Math.Min(span.Length, 13 + QuestProgressPackets.BonusStatEchoLength)].ToArray();

        // Parse the wire vector (truncated entries become a shorter list, which prices to BadState), then let the
        // Application flow price, persist-first and mirror. The host maps the outcome to the 2680 code and owns the
        // runtime mirror and the 2595/2498 sends, which stay byte-identical.
        var request = ParseRequest(span, count);
        var data = new CharacterGameData(services.GetRequiredService<GameDataStore>());
        var res = await BonusStatAllocationFlow.ApplyAsync(player.Character, player.Level, request,
            data, services.GetRequiredService<ICharacterRepository>());
        ushort result = MapResult(res.Result);

        if (res.Applied)
        {
            var character = player.Character!;
            player.Str = character.Str; player.Agi = character.Agi; player.Int = character.Int;
            player.Wis = character.Wis; player.Vit = character.Vit; player.Luk = character.Luk;

            // Re-push the 2498 sheet: it carries the effective primary (blob+14) AND the allocated bonus
            // per stat (blob+26..36), so the client updates the Basic and Bonus columns immediately.
            // A 2498 goes out before the 2680 of every accepted request, and none for a refused one.
            // It is preceded by a 2595 OnPcInitTrait: its u16 at blob+14/+22 carries 1 + the new bonus of the
            // allocated stat, and its blob+72/+76 equal that 2498's current HP/SP. BuildPcInitTrait2595 builds exactly
            // that from the updated character and the 2498 below.
            var equipped = await services.GetRequiredService<IInventoryRepository>().GetEquipmentAsync(character.Id);
            // Combat reads CACHED gear-adjusted stats, so refresh them here too: allocating points changes
            // AGI→Crit, STR/INT→Parry, VIT→HP/DEF. Without this the 2498 sheet shows the new values while
            // combat keeps rolling the OLD crit/parry and dealing the OLD damage until the next login/equip.
            // A VIT/WIS point raises the max, so the runtime max follows it before the 2498 goes out; left
            // at the old value, RegenService capped HP there and every 2401 snapped the bar back. The sheet,
            // the max and the combat cache keep the active effects (EffectService.RefreshPcTraitSheet).
            var trait = services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, character, equipped);
            await session.SendInternalRmiAsync(
                ZoneS2COpcodes.OnPcInitTrait, TraitPackets.BuildPcInitTrait2595(character, trait));
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, trait);

            Logger.LogInformation("{Name} allocated bonus points ({Count} entries, cost {Cost}): STR/AGI/INT/WIS/VIT/LUK bonus now {S}/{A}/{I}/{W}/{V}/{L} ({Left} left)",
                player.Name, count, res.TotalCost, character.BonusStr, character.BonusAgi, character.BonusInt,
                character.BonusWis, character.BonusVit, character.BonusLuk, res.Unspent - res.TotalCost);
        }
        else if (res.SaveError is not null)
        {
            Logger.LogWarning(res.SaveError, "{Name}: could not persist stat allocation; nothing applied or sent", player.Name);
        }
        else if (res.Evaluated && res.Result != BonusStatOutcome.Ok)
        {
            Logger.LogInformation("{Name}: bonus-point request refused (result {Result}, cost {Cost}, unspent {Unspent})",
                player.Name, result, res.TotalCost, res.Unspent);
        }

        // OnAnsUseBonusStatPoint 2680 (COMPACT-BA, msgTag 58423, body 32): self handle, the result code the
        // client reads at blob+9 (0 applied, 3 "Too few bonus points."),
        // and the request echo. Always sent so the request never hangs.
        await session.SendRmiAsync(ZoneS2COpcodes.OnAnsUseBonusStatPoint,
            writer => writer.WriteBytes(QuestProgressPackets.BuildBonusStatPoint2680(player.EntityId, result, echo)));
    }

    /// <summary>
    /// Parses the allocation vector: <paramref name="count"/> entries at payload+14 stride 3 = [u8 statId][u16 amount LE].
    /// An entry that would overrun the body is dropped, leaving fewer entries than the count, which prices to BadState.
    /// </summary>
    private static BonusRequest ParseRequest(ReadOnlySpan<byte> span, int count)
    {
        var entries = new List<BonusEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int off = 14 + 3 * i;
            if (off + 3 > span.Length) break;
            entries.Add(new BonusEntry(span[off], span[off + 1] | (span[off + 2] << 8)));
        }
        return new BonusRequest(count, entries);
    }

    /// <summary>Maps a priced outcome to its 2680 wire result code (0 ok, 1 bad state, 2 over cap, 3 too few points).</summary>
    private static ushort MapResult(BonusStatOutcome outcome) => outcome switch
    {
        BonusStatOutcome.Ok => QuestProgressPackets.BonusStatResultOk,
        BonusStatOutcome.OverCap => QuestProgressPackets.BonusStatResultOverCap,
        BonusStatOutcome.TooFewPoints => QuestProgressPackets.BonusStatResultTooFewPoints,
        _ => QuestProgressPackets.BonusStatResultBadState,
    };
}
