using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1685 ReqJumpingLevelup: use a Level Jumping item, which raises the class level (Item_Type 122) or the
/// job level (Item_Type 123) to the item's Item_Type_Option.
/// </summary>
/// <remarks>
/// <para>
/// TRIGGER. The client switches on the item's Item_Type on a double click. Type 122 opens a confirm
/// dialog with string_ui 15177 "Will you change Class level to @1?", type 123 opens one with 15178 "... Job level
/// to @1?". The @1 is the item info's Item_Type_Option. Both first run the
/// same player check. When it fails the client shows 15179 itself and sends nothing. On OK
/// the client sends the 1685.
/// </para>
/// <para>
/// REQUEST, 8 bytes with no envelope: <c>[u64 itemUid]</c>. ANSWER, 3017 AnsJumpingLevelup, 2 bytes with no
/// envelope: <c>[u16 result]</c>. The client handles it as follows:
/// <list type="bullet">
/// <item>0 returns with no message. The level change itself comes in the server's own level-up messages.</item>
/// <item>25203 shows notification string_ui 10418 "Unable to find Target item".</item>
/// <item>25204 and 25205 show dialog 15179 "It cannot be used in current level." with button 113 "OK".
/// The client handles the two the same way.</item>
/// </list>
/// </para>
/// <para>
/// WHAT THE SERVER DOES. The item must be the sender's, in the bag, and a 122/123 row of ItemInfo (22 rows:
/// Item_Type_Option 30 or 50); otherwise 25203. The target level is Item_Type_Option.
/// <list type="bullet">
/// <item>122, class level: already at or above the target answers 25204. Otherwise the server grants exactly
/// the exp that ends at the target with 0 left: BaseExpTable from the current level to target - 1, less the
/// exp held. It goes through CombatService.GrantExpAsync, which sends one 2595/2498/2394/2395/2542[/2726]
/// burst for the whole grant.</item>
/// <item>123, job level: already at or above the target answers 25205. Otherwise the same sum over the job
/// thresholds goes through ProfessionExp.GrantAsync, which sends 2526 and one 2527, the messages used
/// for job progress.</item>
/// </list>
/// The item is then consumed (2329, as ItemUseHandler does it), and 3017 result 0 is sent. The grant goes
/// before the item's 2329, the order ItemUseHandler uses for an
/// item's effects, and the answer goes last. The client shows the same dialog for 25204 and 25205, so 25204 is
/// paired with class level (the first type, 122) and 25205 with job level.
/// </para>
/// </remarks>
public class JumpingLevelupHandler : ZoneRmiHandlerBase<JumpingLevelupHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqJumpingLevelup;
    public override string Name => "JumpingLevelup";

    public JumpingLevelupHandler(IServiceScopeFactory s, ILogger<JumpingLevelupHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>1685 body length: the u64 uid.</summary>
    internal const int BodyLength = 8;

    /// <summary>ItemInfo Item_Type of a class-level jumping item.</summary>
    internal const byte ClassLevelJumpItemType = 122;

    /// <summary>ItemInfo Item_Type of a job-level jumping item.</summary>
    internal const byte JobLevelJumpItemType = 123;

    /// <summary>3017: the item was used (no message).</summary>
    internal const ushort ResultOk = 0;

    /// <summary>3017: string_ui 10418 "Unable to find Target item".</summary>
    internal const ushort ResultNoItem = 25203;

    /// <summary>3017: dialog 15179 "It cannot be used in current level.", sent for the class-level item.</summary>
    internal const ushort ResultClassLevel = 25204;

    /// <summary>3017: the same dialog as <see cref="ResultClassLevel"/>, sent for the job-level item.</summary>
    internal const ushort ResultJobLevel = 25205;

    /// <summary>2329 use source of a 1323 item use (ItemUseHandler).</summary>
    private const byte UseSourceItemUse = 1;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength)
        {
            Logger.LogDebug("{Name} ReqJumpingLevelup body is {Len} bytes, need {Need}", player.Name, payload.Length, BodyLength);
            return;
        }
        ulong uid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var item = await inventory.GetByIdAsync(uid);
        var data = item is null ? null : store.GetItem(item.ItemId);
        if (item is null || item.CharacterId != player.CharacterId || item.StorageType != 0 || item.Count <= 0
            || data is not { Item_Type: ClassLevelJumpItemType or JobLevelJumpItemType })
        {
            Logger.LogInformation("{Name} asked to use jumping item uid {Uid}, which is not a jumping item in their bag",
                player.Name, uid);
            await AnswerAsync(session, ResultNoItem, ct);
            return;
        }

        byte target = (byte)Math.Clamp(data.Item_Type_Option, 0, byte.MaxValue);
        bool classItem = data.Item_Type == ClassLevelJumpItemType;
        // Legality first: a refused use consumes nothing.
        if (!CanUse(classItem, player, target, store))
        {
            var refusal = classItem ? ResultClassLevel : ResultJobLevel;
            Logger.LogInformation("{Name} cannot use jumping item {Item} (type {Type}, target {Target}) at the current level",
                player.Name, item.ItemId, data.Item_Type, target);
            await AnswerAsync(session, refusal, ct);
            return;
        }

        // Consume first (persisted), then grant. If the consume throws, no exp is granted; if the grant fails
        // afterwards the item is already gone (the safe direction: no free levels).
        var remaining = item.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(item.Id); remaining = 0; }
        else { item.Count = remaining; await inventory.UpdateItemAsync(item); }

        bool granted = classItem
            ? await RaiseClassLevelAsync(player, target, store, services)
            : await RaiseJobLevelAsync(session, player, target, store, services);

        // 2329 clears or updates the bag cell (as ItemUseHandler does for a 1323 use); sent after the grant.
        var (bag, slot) = ItemWorkbench.ClientBagSlot(item);
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            writer => writer.WriteBytes(CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, item.Id,
                (byte)Math.Clamp(remaining, 0, 255), UseSourceItemUse)), ct);

        if (!granted)
        {
            Logger.LogWarning("{Name} jumping item {Item} consumed but the level grant did not apply (target {Target})",
                player.Name, item.ItemId, target);
            await AnswerAsync(session, classItem ? ResultClassLevel : ResultJobLevel, ct);
            return;
        }

        Logger.LogInformation("{Name} used jumping item {Item} (type {Type}) to level {Target}",
            player.Name, item.ItemId, data.Item_Type, target);
        await AnswerAsync(session, ResultOk, ct);
    }

    /// <summary>Whether the item can raise the level at all: the pure exp sum is non-zero (target above the current level).</summary>
    internal static bool CanUse(bool classItem, PlayerState player, byte target, GameDataStore store) =>
        ExpNeeded(classItem, player.Level, player.Exp, (player.Character as Character)?.JobLevel,
            (player.Character as Character)?.JobExp ?? 0, target, store.ExpTable) != 0;

    /// <summary>Exp that ends at <paramref name="target"/> with 0 left; 0 when already there or no job snapshot.</summary>
    internal static ulong ExpNeeded(bool classItem, byte level, ulong exp, byte? jobLevel, ulong jobExp, byte target,
        IReadOnlyDictionary<byte, ExpTableEntry> table)
    {
        if (classItem) return JobExpRules.ExpToReach(level, exp, target, table);
        return jobLevel is { } jl ? JobExpRules.ExpToReach(jl, jobExp, target, table) : 0;
    }

    /// <summary>Class level: grant exactly the exp that ends at <paramref name="target"/> with 0 left.</summary>
    private static async Task<bool> RaiseClassLevelAsync(PlayerState player, byte target, GameDataStore store, IServiceProvider services)
    {
        ulong needed = JobExpRules.ExpToReach(player.Level, player.Exp, target, store.ExpTable);
        if (needed == 0) return false;
        await services.GetRequiredService<CombatService>().GrantExpAsync(player, needed);
        return true;
    }

    /// <summary>Job level: the same sum over the job thresholds, through the 2526/2527 path.</summary>
    private async Task<bool> RaiseJobLevelAsync(ProudSession session, PlayerState player, byte target, GameDataStore store,
        IServiceProvider services)
    {
        if (player.Character is not Character character) return false;
        ulong needed = JobExpRules.ExpToReach(character.JobLevel, character.JobExp, target, store.ExpTable);
        if (needed == 0) return false;
        return await ProfessionExp.GrantAsync(session, player, needed, store, Broadcast,
            services.GetRequiredService<ICharacterRepository>(), Logger, services);
    }

    private static Task AnswerAsync(ProudSession session, ushort result, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsJumpingLevelup, writer => writer.WriteUInt16(result), ct);
}
