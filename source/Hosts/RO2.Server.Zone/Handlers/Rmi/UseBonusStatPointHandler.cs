using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

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

        var character = player.Character;
        // The snapshot's Level is the login one: a level-up moves only PlayerState.Level (CombatService), so the
        // points earned this session (BonusStatPointsEarned below) and the 2498 sheet would read the old level.
        if (character is not null && player.Level != 0 && character.Level != player.Level)
            character.Level = player.Level;
        var store = services.GetRequiredService<GameDataStore>();
        ushort result = QuestProgressPackets.BonusStatResultBadState;

        if (character is not null && StatService.ClassBasePrimaries(store, character.Job) is { } cb)
        {
            // Price the WHOLE request on a scratch copy before touching the character. A request is never
            // applied in part: when the pool cannot cover it, it is answered with
            // result 3 and no 2498, and the next accepted request starts from the unchanged pool.
            //
            // No Max_Status cap on the allocation: Max_Status never exceeds 99 at any level, so that table does not
            // bound what can be allocated. The per-point cost comes from Status_Point (1 per point up to 998); its 0
            // at the end of the table is the stat's upper bound and is answered with result 2.
            int unspent = StatService.BonusStatPointsEarned(character.Level) - BonusOf(character).Sum();
            var plan = Price(span, count, BonusOf(character), unspent, bonusNow => StatService.BonusStatCost(store, bonusNow));
            result = plan.Result;
            int totalCost = plan.TotalCost;
            int[] bonus = plan.Bonus;

            if (result == QuestProgressPackets.BonusStatResultOk && totalCost > 0)
            {
                // PERSIST FIRST, THEN MIRROR (the ChangeProJobIDHandler order). The allocation is written to the
                // character and saved before the runtime copy or any packet moves; if the save fails the
                // character is put back, the runtime is untouched, and the client gets the 2680 refusal alone, so
                // what it shows can never be ahead of the database (a relog used to revert it).
                var saveError = await PersistAsync(character, cb, bonus,
                    c => services.GetRequiredService<ICharacterRepository>().SaveCharacterAsync(c));
                if (saveError is not null)
                {
                    result = QuestProgressPackets.BonusStatResultBadState;
                    Logger.LogWarning(saveError, "{Name}: could not persist stat allocation; nothing applied or sent", player.Name);
                }
                else
                {
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
                        ZoneS2COpcodes.OnPcInitTrait, WorldEntryService.BuildPcInitTrait2595(character, trait));
                    await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, trait);

                    Logger.LogInformation("{Name} allocated bonus points ({Count} entries, cost {Cost}): STR/AGI/INT/WIS/VIT/LUK bonus now {S}/{A}/{I}/{W}/{V}/{L} ({Left} left)",
                        player.Name, count, totalCost, character.BonusStr, character.BonusAgi, character.BonusInt,
                        character.BonusWis, character.BonusVit, character.BonusLuk, unspent - totalCost);
                }
            }
            else if (result != QuestProgressPackets.BonusStatResultOk)
            {
                Logger.LogInformation("{Name}: bonus-point request refused (result {Result}, cost {Cost}, unspent {Unspent})",
                    player.Name, result, totalCost, unspent);
            }
        }

        // OnAnsUseBonusStatPoint 2680 (COMPACT-BA, msgTag 58423, body 32): self handle, the result code the
        // client reads at blob+9 (0 applied, 3 "Too few bonus points."),
        // and the request echo. Always sent so the request never hangs.
        await session.SendRmiAsync(ZoneS2COpcodes.OnAnsUseBonusStatPoint,
            writer => writer.WriteBytes(QuestProgressPackets.BuildBonusStatPoint2680(player.EntityId, result, echo)));
    }

    /// <summary>The priced outcome of one 1493: the result code, the six bonus values it would leave (STR, AGI, INT, WIS, VIT, LUK)
    /// and what it would cost.</summary>
    internal readonly record struct BonusPlan(ushort Result, int[] Bonus, int TotalCost);

    internal static int[] BonusOf(Character c) => [c.BonusStr, c.BonusAgi, c.BonusInt, c.BonusWis, c.BonusVit, c.BonusLuk];

    /// <summary>
    /// Prices the WHOLE request against <paramref name="unspent"/> without touching anything: the result is
    /// <c>Ok</c> only when every entry fits, otherwise the first failing code (BadState for a short or malformed
    /// vector, OverCap for a stat id above 5 or a stat at the end of Status_Point, TooFewPoints past the pool).
    /// <paramref name="costOf"/> maps a stat's current bonus to the points one more costs (0 = cannot be raised).
    /// </summary>
    internal static BonusPlan Price(ReadOnlySpan<byte> span, int count, int[] currentBonus, int unspent, Func<int, int> costOf)
    {
        var bonus = (int[])currentBonus.Clone();
        int totalCost = 0;
        ushort result = QuestProgressPackets.BonusStatResultOk;
        for (int i = 0; i < count && result == QuestProgressPackets.BonusStatResultOk; i++)
        {
            int off = 14 + 3 * i;
            if (off + 3 > span.Length) { result = QuestProgressPackets.BonusStatResultBadState; break; }
            int statType = span[off];
            int amount = span[off + 1] | (span[off + 2] << 8);
            if (statType > 5) { result = QuestProgressPackets.BonusStatResultOverCap; break; }
            for (int k = 0; k < amount; k++)
            {
                int cost = costOf(bonus[statType]);
                if (cost <= 0) { result = QuestProgressPackets.BonusStatResultOverCap; break; }
                totalCost += cost;
                bonus[statType]++;
                // Past the pool the request cannot be accepted whatever follows; stopping here also
                // bounds the work a hostile count/amount pair can ask for.
                if (totalCost > unspent) { result = QuestProgressPackets.BonusStatResultTooFewPoints; break; }
            }
        }
        return new BonusPlan(result, bonus, totalCost);
    }

    /// <summary>
    /// Writes the priced allocation (bonuses and effective primaries = class base + bonus) onto
    /// <paramref name="c"/> and saves it BEFORE anything else moves. Returns null when the save went through. When it
    /// throws, the character is put back exactly as it was and the exception is returned, so the caller leaves the
    /// runtime copy alone and refuses the request.
    /// </summary>
    internal static async Task<Exception?> PersistAsync(Character c,
        (int str, int agi, int intel, int wis, int vit, int luk) cb, int[] bonus, Func<Character, Task> save)
    {
        var oldBonus = BonusOf(c);
        var oldPrimaries = (c.Str, c.Agi, c.Int, c.Wis, c.Vit, c.Luk);
        c.BonusStr = bonus[0]; c.BonusAgi = bonus[1]; c.BonusInt = bonus[2];
        c.BonusWis = bonus[3]; c.BonusVit = bonus[4]; c.BonusLuk = bonus[5];
        c.Str = cb.str + c.BonusStr; c.Agi = cb.agi + c.BonusAgi;
        c.Int = cb.intel + c.BonusInt; c.Wis = cb.wis + c.BonusWis;
        c.Vit = cb.vit + c.BonusVit; c.Luk = cb.luk + c.BonusLuk;
        try
        {
            await save(c);
            return null;
        }
        catch (Exception ex)
        {
            c.BonusStr = oldBonus[0]; c.BonusAgi = oldBonus[1]; c.BonusInt = oldBonus[2];
            c.BonusWis = oldBonus[3]; c.BonusVit = oldBonus[4]; c.BonusLuk = oldBonus[5];
            (c.Str, c.Agi, c.Int, c.Wis, c.Vit, c.Luk) = oldPrimaries;
            return ex;
        }
    }
}
