using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1608 (ReqThemeCostumeCompose). Fuses two theme costumes on a compose deck into a new costume. A keep charm
/// keeps the appearance or the runes of one of the costumes. The answer is AnsThemeCostumeCompose (2896).
/// </summary>
/// <remarks>
/// <para>
/// Request layout: <c>[u32 length - 4][deck][costume][costume][u16 charmCount][charm...][u64 keptUid][u8][u32][u32 deckTemplate]</c>,
/// where each item record is <c>[u8 slot][u8 bag][u8 1][u64 uid][u32]</c>. The trailing deck template is the compose
/// deck's item id.
/// </para>
/// <para>
/// On success the server sends one UseItem (2329) per consumed input, a PickupItem (2333) for the new costume, and
/// AnsThemeCostumeCompose (2896) with the new item record. A refusal is a 2896 with a non-zero result and a zeroed
/// record, and no bag change is made.
/// </para>
/// <para>
/// Without a keep-appearance charm, the result is drawn from the whole compose table, weighted by each row's ratio.
/// With one, the result keeps the compose row of the kept costume. The socket variant is drawn from the deck's socket
/// ratios; with a keep-runes charm it is never below the kept costume's socket.
/// </para>
/// <list type="bullet">
/// <item><see cref="ResultDeckMismatch"/>: the deck item is in the bag but is not the trailing deck template.</item>
/// <item><see cref="ResultKeptCostumeNoRow"/>: a keep charm is used and the kept costume has no compose row.</item>
/// <item><see cref="ResultNotComposable"/>: any other invalid input, such as a missing, duplicate or foreign item.</item>
/// </list>
/// <para>
/// The client shows a message only for <see cref="ResultNotComposable"/>.
/// </para>
/// </remarks>
public class ThemeCostumeComposeHandler : ZoneRmiHandlerBase<ThemeCostumeComposeHandler>
{
 /// <summary>2896 result for inputs that cannot be composed.</summary>
    internal const ushort ResultNotComposable = 23053;
 /// <summary>2896 result when the deck item is in the bag but is not the trailing deck template.</summary>
    internal const ushort ResultDeckMismatch = 23052;
 /// <summary>2896 result when a keep charm is used and the kept costume has no compose row.</summary>
    internal const ushort ResultKeptCostumeNoRow = 1;
    private const byte ItemTypeCostume = 3;           
    private const byte ItemTypeKeepAppearance = 47;   
    private const byte ItemTypeKeepRunes = 48;        
    private const byte UseSourceServer = 3;           
    private const int RecordSize = 15;
    private const int SocketCount = 4;
 /// <summary>The 2896 body is a u16 result followed by one 126-byte item record: 128 bytes in all.</summary>
    private const int ItemRecordSize = 126;

    public override ushort Opcode => ZoneC2SOpcodes.ReqThemeCostumeCompose;
    public override string Name => "ThemeCostumeCompose";

    public ThemeCostumeComposeHandler(IServiceScopeFactory s, ILogger<ThemeCostumeComposeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var store = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();

        var request = ParseRequest(payload.Span);
        var (plan, refusal) = request is null
            ? (null, ResultNotComposable)
            : await PlanAsync(request, player.CharacterId, store, inventory);
        if (plan is null)
        {
            Logger.LogInformation("{Name}: theme costume compose refused with {Code} ({Why})", player.Name, refusal,
                request is null ? "malformed request" : refusal switch
                {
                    ResultDeckMismatch => "the deck record's item is not the deck template",
                    ResultKeptCostumeNoRow => "the kept costume has no ComposeInfo row",
                    _ => "an item missing, foreign or not composable",
                });
            await SendRefusalAsync(session, refusal, ct);
            return;
        }

        
        var row = plan.KeepAppearance ? plan.KeptRow! : DrawRow(store.ComposeInfo);
        if (row is null)
        {
            Logger.LogWarning("{Name}: no ComposeInfo row with a non-zero Ratio, compose refused", player.Name);
            await SendRefusalAsync(session, ResultNotComposable, ct);
            return;
        }
        int socket = DrawSocket(plan.Ratios, plan.KeepRunes ? plan.KeptSocket : 0);
        uint resultId = SocketItem(row, socket);

        
        var useBodies = new List<byte[]>(plan.Inputs.Count);
        foreach (var it in plan.Inputs)
        {
            var (bag, slot) = ItemWorkbench.ClientBagSlot(it);
            it.Count = Math.Max(it.Count, 1) - 1;   
            if (it.Count == 0) await inventory.RemoveItemAsync(it.Id);
            else await inventory.UpdateItemAsync(it);
            useBodies.Add(CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, it.Id,
                (byte)Math.Clamp(it.Count, 0, byte.MaxValue), UseSourceServer));
        }

        
        var stackMax = store.GetItem(resultId)?.Stack_Max ?? (byte)1;
        var placed = await InventoryStacking.AddAsync(inventory, player.CharacterId, resultId, 1, stackMax);
        var record = placed.Count > 0 ? ItemPackets.BuildItemRecord126(placed[0].Stack) : null;

        Logger.LogInformation("{Name}: theme costume compose -> {Result} (theme {Theme}, slot type {Type}, socket {Socket}; keep appearance {KA}, keep runes {KR})",
            player.Name, resultId, row.Theme_Type, row.Type, socket, plan.KeepAppearance, plan.KeepRunes);

        foreach (var body in useBodies)
            await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(body), ct);
        if (record is not null)
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                writer => writer.WriteBytes(LootNotifyPackets.BuildPickupItem2333(record)), ct);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsThemeCostumeCompose, writer =>
        {
            writer.WriteUInt16(0);
            if (record is not null) writer.WriteBytes(record);
            else writer.WriteZeros(ItemRecordSize);
        }, ct);
    }

 /// <summary>Refuses a request with <paramref name="result"/> and a zeroed record. No bag change is made.</summary>
    private static Task SendRefusalAsync(ProudSession session, ushort result, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsThemeCostumeCompose, BuildRefusal2896(result), ct);

 /// <summary>The refusal body, <c>[u16 result][126 zero bytes]</c>.</summary>
    internal static byte[] BuildRefusal2896(ushort result)
    {
        var body = new byte[2 + ItemRecordSize];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

    internal sealed record Request(ulong DeckUid, ulong CostumeA, ulong CostumeB, List<ulong> Charms, ulong KeptUid, uint DeckTemplate);

    internal sealed record Plan(List<InventoryItem> Inputs, ComposeRandomInfo Ratios,
        bool KeepAppearance, bool KeepRunes, ComposeInfo? KeptRow, int KeptSocket);

 /// <summary>Parses the request. Returns null unless the body is exactly <c>68 + 15k</c> bytes with a matching length prefix.</summary>
    internal static Request? ParseRequest(ReadOnlySpan<byte> body)
    {
        const int head = 4 + 3 * RecordSize;             
        if (body.Length < head + 2 + 17) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != (uint)(body.Length - 4)) return null;

        static ulong UidAt(ReadOnlySpan<byte> b, int recordOffset) => BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(recordOffset + 3));

        int k = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(head));
        if (k > 2 || body.Length != head + 2 + k * RecordSize + 17) return null;   

        var charms = new List<ulong>(k);
        for (int i = 0; i < k; i++) charms.Add(UidAt(body, head + 2 + i * RecordSize));
        int tail = head + 2 + k * RecordSize;             
        return new Request(
            DeckUid: UidAt(body, 4),
            CostumeA: UidAt(body, 4 + RecordSize),
            CostumeB: UidAt(body, 4 + 2 * RecordSize),
            Charms: charms,
            KeptUid: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(tail)),
            DeckTemplate: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(tail + 13)));
    }

 /// <summary>
 /// Resolves the request against the character's bag and the data tables. Returns the plan, or null and the 2896
 /// result to refuse with; nothing changes in that case.
 /// </summary>
    internal static async Task<(Plan? Plan, ushort Refusal)> PlanAsync(Request req, uint characterId, GameDataStore store, IInventoryRepository inventory)
    {
        static (Plan?, ushort) Refuse(ushort code) => (null, code);

        var seen = new HashSet<ulong>();
        async Task<InventoryItem?> LoadAsync(ulong uid)
        {
            if (uid == 0 || !seen.Add(uid)) return null;
            var it = await inventory.GetByIdAsync(uid);
            return it is not null && it.CharacterId == characterId && it.StorageType == 0 ? it : null;
        }

        if (await LoadAsync(req.DeckUid) is not { } deck) return Refuse(ResultNotComposable);
        if (deck.ItemId != req.DeckTemplate) return Refuse(ResultDeckMismatch);
        var ratios = store.ComposeRandomInfo.FirstOrDefault(r => r.ItemID == deck.ItemId);
        if (ratios is null) return Refuse(ResultNotComposable);

        if (await LoadAsync(req.CostumeA) is not { } costumeA || await LoadAsync(req.CostumeB) is not { } costumeB)
            return Refuse(ResultNotComposable);
        if (store.GetItem(costumeA.ItemId)?.Item_Type != ItemTypeCostume || store.GetItem(costumeB.ItemId)?.Item_Type != ItemTypeCostume)
            return Refuse(ResultNotComposable);

        var inputs = new List<InventoryItem> { deck, costumeA, costumeB };
        bool keepAppearance = false, keepRunes = false;
        foreach (var uid in req.Charms)
        {
            if (await LoadAsync(uid) is not { } charm) return Refuse(ResultNotComposable);
            switch (store.GetItem(charm.ItemId)?.Item_Type)
            {
                case ItemTypeKeepAppearance when !keepAppearance: keepAppearance = true; break;
                case ItemTypeKeepRunes when !keepRunes: keepRunes = true; break;
                default: return Refuse(ResultNotComposable);
            }
            inputs.Add(charm);
        }

        ComposeInfo? keptRow = null;
        int keptSocket = 0;
        if (keepAppearance || keepRunes)
        {
            var kept = req.KeptUid == costumeA.Id ? costumeA : req.KeptUid == costumeB.Id ? costumeB : null;
            if (kept is null) return Refuse(ResultNotComposable);
            foreach (var r in store.ComposeInfo)
            {
                int s = Array.IndexOf(new[] { r.Item_Socket_0, r.Item_Socket_1, r.Item_Socket_2, r.Item_Socket_3 }, kept.ItemId);
                if (s >= 0) { keptRow = r; keptSocket = s; break; }
            }
            if (keptRow is null) return Refuse(ResultKeptCostumeNoRow);
        }

        return (new Plan(inputs, ratios, keepAppearance, keepRunes, keptRow, keptSocket), 0);
    }

 /// <summary>Draws one compose row, weighted by its ratio. Rows with a ratio of 0 are never drawn.</summary>
    private static ComposeInfo? DrawRow(IReadOnlyList<ComposeInfo> rows)
    {
        long total = 0;
        foreach (var r in rows) total += r.Ratio;
        if (total <= 0) return null;
        long pick = Random.Shared.NextInt64(total);
        foreach (var r in rows)
        {
            pick -= r.Ratio;
            if (pick < 0) return r;
        }
        return null;
    }

 /// <summary>Draws a socket index from <paramref name="minSocket"/> to 3, weighted by the deck's socket ratios.</summary>
    private static int DrawSocket(ComposeRandomInfo ratios, int minSocket)
    {
        var w = new[] { ratios.Ratio_Socket_0, ratios.Ratio_Socket_1, ratios.Ratio_Socket_2, ratios.Ratio_Socket_3 };
        long total = 0;
        for (int s = minSocket; s < SocketCount; s++) total += w[s];
        if (total <= 0) return minSocket;
        long pick = Random.Shared.NextInt64(total);
        for (int s = minSocket; s < SocketCount; s++)
        {
            pick -= w[s];
            if (pick < 0) return s;
        }
        return SocketCount - 1;
    }

    private static uint SocketItem(ComposeInfo row, int socket) => socket switch
    {
        0 => row.Item_Socket_0,
        1 => row.Item_Socket_1,
        2 => row.Item_Socket_2,
        _ => row.Item_Socket_3,
    };
}
