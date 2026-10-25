using System.Buffers.Binary;
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
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Opens the next rune hole of an item with a rune hole puncher (RMI 1542, ReqOpenItemSocket).
/// </summary>
/// <remarks>
/// <para>
/// Request body: <c>[u64 puncherUid][u64 targetUid]</c>. The reply is the 2785 result with the target's updated record.
/// The kind of the new hole comes from the item's socket group (<see cref="ItemWorkbench.HoleKind"/>).
/// </para>
/// <para>
/// The request is refused with a result code when the puncher is missing or is not a puncher, the target is missing,
/// the target has no free hole, its required level is above the puncher's, or the puncher's part does not fit the target
/// (see <see cref="Check"/>).
/// </para>
/// </remarks>
public class OpenItemSocketHandler : ZoneRmiHandlerBase<OpenItemSocketHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqOpenItemSocket;
    public override string Name => "OpenItemSocket";

 /// <summary>Length of the request body in bytes.</summary>
    private const int RequestBodyLength = 16;

 /// <summary>ItemInfo Item_Type of rune hole punchers.</summary>
    private const byte PuncherItemType = 30;

 /// <summary>Result code of a punched hole. The 2785 body carries the target's record with the new hole.</summary>
    internal const ushort Opened = 0;

 /// <summary>Result code: the puncher stack is missing, foreign or empty.</summary>
    internal const ushort NoPuncher = 1;

 /// <summary>Result code: the item in the puncher slot is not a rune hole puncher.</summary>
    internal const ushort NotAPuncher = 2;

 /// <summary>Result code: the target is missing, foreign, or neither in the bag nor worn.</summary>
    internal const ushort NoTarget = 3;

 /// <summary>Result code: the target has no next rune hole to punch.</summary>
    internal const ushort NoMoreHoles = 4;

 /// <summary>Result code: the target's required level is above the puncher's.</summary>
    internal const ushort WrongLevel = 5;

 /// <summary>Result code: the target's part does not match the puncher's (see <see cref="SuitsPart"/>).</summary>
    internal const ushort WrongPart = 6;

    
    
 /// <summary>The target part a puncher opens holes in, derived from its ItemInfo row (see <see cref="PuncherPartOf"/>).</summary>
    internal enum PuncherPart : byte
    {
 /// <summary>Decoration costumes, High_Category 4.</summary>
        Costume = 1,
 /// <summary>Accessories, High_Category 3.</summary>
        Accessory = 2,
 /// <summary>Weapons and armour, High_Category 1 and 2.</summary>
        Gear = 3,
    }

    public OpenItemSocketHandler(IServiceScopeFactory s, ILogger<OpenItemSocketHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength)
        {
            Logger.LogWarning("Session {Id}: OpenItemSocket body is {Len} bytes, not the 16 retail sends",
                session.SessionId, payload.Length);
            return;
        }
        var span = payload.Span;
        ulong puncherUid = BinaryPrimitives.ReadUInt64LittleEndian(span);
        ulong targetUid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8));

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var puncher = puncherUid != 0 && puncherUid != targetUid ? await inventory.GetByIdAsync(puncherUid) : null;
        var target = targetUid != 0 ? await inventory.GetByIdAsync(targetUid) : null;

        ushort code = Check(store, PuncherAbiliVal, player.CharacterId, puncher, target, out int hole, out string why);
        if (code != Opened)
        {
            Logger.LogInformation("{Name} open-socket with puncher {Puncher} on {Target} refused with {Code}: {Why}",
                player.Name, puncherUid, targetUid, code, why);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsOpenItemSocket, BuildRefusal2785(code), ct);
            return;
        }

        Punch(target!, hole, puncher!.ItemId);
        await inventory.UpdateItemAsync(target!);

        
        int left = puncher.Count - 1;
        var (pBag, pSlot) = ItemWorkbench.ClientBagSlot(puncher);
        if (left <= 0) { await inventory.RemoveItemAsync(puncher.Id); left = 0; }
        else { puncher.Count = left; await inventory.UpdateItemAsync(puncher); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, pSlot, pBag, puncher.Id, (byte)Math.Clamp(left, 0, 255), useSource: 1)));

        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsOpenItemSocket,
            BuildAnswer2785(ItemWorkbench.RecordWithRuneholes(target!, store)), ct);

        Logger.LogInformation("{Name} opened hole #{Hole} (kind {Kind}) on '{Item}' ({Uid}) with puncher {Puncher}, {Left} left",
            player.Name, hole + 1, ItemWorkbench.HoleKind(store, target!.ItemId, hole),
            store.GetItem(target.ItemId)?.Name ?? target.ItemId.ToString(), target.Id, puncher.ItemId, left);
    }

 /// <summary>
 /// Returns the 2785 result code that refuses the punch, or <see cref="Opened"/> with the index of the hole to open in <paramref name="hole"/>.
 /// </summary>
 /// <remarks>
 /// Checks run in this order: puncher stack, puncher kind, target, next hole, level, part.
 /// <paramref name="abiliValOf"/> returns a template's ItemInfo Abili_val, which tells accessory punchers from gear punchers.
 /// It returns null when the value is unknown.
 /// </remarks>
    internal static ushort Check(GameDataStore store, Func<uint, int?> abiliValOf, uint characterId,
        InventoryItem? puncher, InventoryItem? target, out int hole, out string why)
    {
        hole = 0;
        why = "";
        if (puncher is null || puncher.CharacterId != characterId || puncher.StorageType != 0 || puncher.Count < 1)
        {
            why = "puncher is not a stack in the bag";
            return NoPuncher;
        }
        var puncherInfo = store.GetItem(puncher.ItemId);
        if (puncherInfo is null || puncherInfo.Item_Type != PuncherItemType)
        {
            why = $"item {puncher.ItemId} is not a runehole puncher (Item_Type {PuncherItemType})";
            return NotAPuncher;
        }
        if (target is null || target.CharacterId != characterId || target.StorageType is not (0 or 1))
        {
            why = "target is not in the bag or worn";
            return NoTarget;
        }
        hole = ItemWorkbench.OpenedHoles(target);
        if (ItemWorkbench.HoleKind(store, target.ItemId, hole) == 0)
        {
            why = $"item {target.ItemId} has no hole #{hole + 1} to open";
            return NoMoreHoles;
        }
        
        var targetInfo = store.GetItem(target.ItemId)!;
        if (targetInfo.Require_Level > puncherInfo.Require_Level)
        {
            why = $"item {target.ItemId} needs level {targetInfo.Require_Level}, puncher {puncher.ItemId} reaches {puncherInfo.Require_Level}";
            return WrongLevel;
        }
        if (abiliValOf(puncher.ItemId) is int abili
            && !SuitsPart(PuncherPartOf(puncherInfo.Require_Level, abili), targetInfo.High_Category))
        {
            why = $"puncher {puncher.ItemId} ({PuncherPartOf(puncherInfo.Require_Level, abili)}) does not open High_Category {targetInfo.High_Category}";
            return WrongPart;
        }
        return Opened;
    }

 /// <summary>
 /// Returns the part a puncher is for. No data column names it directly, so it is derived from ItemInfo.
 /// </summary>
 /// <remarks>
 /// Require_Level 0 is a costume puncher, Abili_val 0 with a level is an accessory puncher, and anything else is a gear puncher.
 /// </remarks>
    internal static PuncherPart PuncherPartOf(byte requireLevel, int abiliVal) =>
        requireLevel == 0 ? PuncherPart.Costume
        : abiliVal == 0 ? PuncherPart.Accessory
        : PuncherPart.Gear;

 /// <summary>
 /// Whether a puncher of <paramref name="part"/> can open a hole in an item of ItemInfo High_Category <paramref name="highCategory"/>.
 /// </summary>
    internal static bool SuitsPart(PuncherPart part, byte highCategory) => part switch
    {
        PuncherPart.Costume => highCategory == 4,
        PuncherPart.Accessory => highCategory == 3,
        _ => highCategory is 1 or 2,
    };

 /// <summary>
 /// Opens hole <paramref name="hole"/> of <paramref name="target"/>: adds an empty entry for it and records the puncher's template for that hole.
 /// </summary>
    internal static void Punch(InventoryItem target, int hole, uint puncherTemplate)
    {
        var holes = StatService.ParseSockets(target.CardsJson);
        Array.Resize(ref holes, hole + 1);
        holes[hole] = 0;
        target.CardsJson = StatService.SerializeSockets(holes);
        var punchers = StatService.ParseSockets(target.SocketPunchersJson);
        Array.Resize(ref punchers, hole + 1);
        punchers[hole] = puncherTemplate;
        target.SocketPunchersJson = StatService.SerializeSockets(punchers);
    }

 /// <summary>Builds the 2785 success reply: a zero result followed by the 126-byte record of the target.</summary>
    internal static byte[] BuildAnswer2785(byte[] targetRecord126)
    {
        var body = new byte[2 + ItemPackets.ItemRecordLength];
        targetRecord126.AsSpan(0, ItemPackets.ItemRecordLength).CopyTo(body.AsSpan(2));
        return body;
    }

 /// <summary>Builds the 2785 refusal reply: the result code followed by 126 zero bytes.</summary>
    internal static byte[] BuildRefusal2785(ushort code)
    {
        var body = new byte[2 + ItemPackets.ItemRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, code);
        return body;
    }

 /// <summary>Returns the ItemInfo Abili_val of a puncher template, or null when the table could not be read.</summary>
    private static int? PuncherAbiliVal(uint templateId)
    {
        var table = AbiliValTable.Value;
        return table is null ? null : table.TryGetValue(templateId, out var v) ? v : 0;
    }

 /// <summary>ItemInfo columns that ItemData does not carry, read once from the game data directory.</summary>
    private sealed record AbiliValRow(uint ID, byte Item_Type, int Abili_val);

    private static readonly Lazy<IReadOnlyDictionary<uint, int>?> AbiliValTable =
        new(() => LoadAbiliVal(Path.Combine("data", "ItemInfo.json")));

 /// <summary>
 /// Returns the Abili_val of every Item_Type-30 row in <paramref name="path"/>, or null when the file cannot be read.
 /// </summary>
    internal static IReadOnlyDictionary<uint, int>? LoadAbiliVal(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<AbiliValRow>>(File.ReadAllBytes(path));
            var byId = new Dictionary<uint, int>();
            var seen = new HashSet<uint>();
            foreach (var r in rows ?? new List<AbiliValRow>())
                if (seen.Add(r.ID) && r.Item_Type == PuncherItemType) byId[r.ID] = r.Abili_val;
            return byId;
        }
        catch
        {
            return null;
        }
    }
}
