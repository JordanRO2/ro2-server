using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles ReqIllusionPyramidGetMyState: returns the player's pyramid balances and top record. The client asks when the window opens and again before every stage draw.
/// </summary>
/// <remarks>
/// <para>
/// The reply, AnsIllusionPyramidGetMyState (2906), is a fixed 32-byte record: a u16 result, the two mileage rows of Mileage_Type 1 (the Mystical and Bright Phantom Stone prizes), the Phantom Stone item id, the Mystical and Bright balances (u16 each), the Phantom Stones in the bag (u32), and the top record (u8, the highest floor reached).
/// </para>
/// <para>
/// The result is a near-cap warning: 29018 when the Mystical balance is at or above 990, 29019 when the Bright balance is, 29020 when both are, and 0 otherwise.
/// </para>
/// <para>
/// Phantom Stones in the bag are counted from StorageType 0 only; the bank is not counted. The Mystical and Bright balances are server-side and are not bag items. They change only through pyramid settlements in <see cref="IllusionPyramidService"/>. A character with no pyramid record reads zeros.
/// </para>
/// </remarks>
public class IllusionPyramidGetMyStateHandler : ZoneRmiHandlerBase<IllusionPyramidGetMyStateHandler>
{
 /// <summary>
 /// Mileage_Type of the pyramid's mileage reward rows.
 /// </summary>
    private const byte PyramidMileageType = 1;

 /// <summary>
 /// Phantom Stone item id (ItemInfo 60000377). The pyramid uses it as a constant; no data table carries it.
 /// </summary>
    internal const uint PhantomStoneItemId = 60000377;

 /// <summary>
 /// Expected 2906 body length, in bytes.
 /// </summary>
    internal const int MyStateLength = 32;

 /// <summary>
 /// StorageType of the character's bag rows; the bank and worn gear use other values.
 /// </summary>
    private const byte BagStorageType = 0;

 /// <summary>
 /// Number of mileage rows in the 2906 body (Mystical, then Bright), each written as a Mileage_ID u32 and a prize item u32.
 /// </summary>
    private const int MileageRowCount = 2;

 /// <summary>
 /// Length of one mileage row in the 2906 body: Mileage_ID u32 + prize item u32.
 /// </summary>
    private const int MileageRowLength = 2 * sizeof(uint);

 /// <summary>
 /// Value of the u8 at body offset +2, which carries no data.
 /// </summary>
    private const byte UnusedByte = 0;

 /// <summary>
 /// 2906 result 0: no warning.
 /// </summary>
    internal const ushort NoWarningResult = 0;

 /// <summary>
 /// 2906 result 29018: the Mystical balance is at or above <see cref="NearCapFrom"/>, and the Bright balance is below it.
 /// </summary>
    internal const ushort MysticalNearCapResult = 29018;

 /// <summary>
 /// 2906 result 29019: the Bright balance is at or above <see cref="NearCapFrom"/>, and the Mystical balance is below it.
 /// </summary>
    internal const ushort BrightNearCapResult = 29019;

 /// <summary>
 /// 2906 result 29020: both balances are at or above <see cref="NearCapFrom"/>.
 /// </summary>
    internal const ushort BothNearCapResult = 29020;

 /// <summary>
 /// The balance at which the near-cap warning starts.
 /// </summary>
    internal const ushort NearCapFrom = 990;

    public override ushort Opcode => ZoneC2SOpcodes.ReqIllusionPyramidGetMyState;
    public override string Name => "IllusionPyramidGetMyState";

    public IllusionPyramidGetMyStateHandler(IServiceScopeFactory s, ILogger<IllusionPyramidGetMyStateHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        await IllusionPyramidService.NoteStateRequestAsync(player, ct);

        var mileage = services.GetRequiredService<GameDataStore>().Mileage
            .Where(m => m.Mileage_Type == PyramidMileageType)
            .OrderBy(m => m.Mileage_ID)
            .Take(MileageRowCount)
            .ToList();

        
        
        var items = await services.GetRequiredService<IInventoryRepository>().GetByCharacterAsync(player.CharacterId);
        uint stones = CountInBag(items, PhantomStoneItemId);

        
        
        
        var (mystical, bright, topRecord) = await IllusionPyramidService.GetStateAsync(player, services, Logger, ct);
        var body = BuildMyState2906(mileage, mystical, bright, stones, topRecord);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidGetMyState, writer => writer.WriteBytes(body), ct);
    }

 /// <summary>
 /// Counts the Phantom Stones the character carries in the bag (StorageType 0), excluding the bank and worn gear.
 /// </summary>
 /// <see cref="IllusionPyramidService.DrawAsync"/> refuses a Phantom Stone start on the same count, so the client and the server agree.
    internal static uint CountInBag(IEnumerable<InventoryItem> items, uint itemId)
    {
        long total = 0;
        foreach (var item in items)
        {
            if (item.StorageType == BagStorageType && item.ItemId == itemId && item.Count > 0)
                total += item.Count;
        }
        return (uint)Math.Min(total, uint.MaxValue);
    }

 /// <summary>
 /// Builds the 32-byte 2906 body. Mileage rows are written in Mileage_ID order; a missing row is written as 8 zero bytes.
 /// </summary>
    internal static byte[] BuildMyState2906(IReadOnlyList<Mileage> pyramidMileage, ushort mysticalStones,
        ushort brightStones, uint phantomStones, byte topRecord)
    {
        using var w = new PacketWriter(MyStateLength);
        w.WriteUInt16(NearCapResult(mysticalStones, brightStones)); // +0 result
        w.WriteByte(UnusedByte);                                    // +2
        for (int i = 0; i < MileageRowCount; i++)
        {
            if (i < pyramidMileage.Count)
            {
                w.WriteUInt32(pyramidMileage[i].Mileage_ID); // +3 / +11
                w.WriteUInt32(pyramidMileage[i].Item);       // +7 / +15
            }
            else
            {
                w.WriteZeros(MileageRowLength);
            }
        }
        w.WriteUInt32(PhantomStoneItemId); // +19
        w.WriteUInt16(mysticalStones);     // +23 Mystical Phantom Stones held
        w.WriteUInt16(brightStones);       // +25 Bright Phantom Stones held
        w.WriteUInt32(phantomStones);      // +27 Phantom Stones in the bag
        w.WriteByte(topRecord);            // +31 top record (highest floor reached)
        return w.GetWrittenSpan().ToArray();
    }

 /// <summary>
 /// Returns the 2906 result for the two balances: 0 when both are below <see cref="NearCapFrom"/>, otherwise the near-cap warning for the balance or balances at or above it.
 /// </summary>
    internal static ushort NearCapResult(ushort mysticalStones, ushort brightStones)
    {
        bool mystical = mysticalStones >= NearCapFrom;
        bool bright = brightStones >= NearCapFrom;
        return (mystical, bright) switch
        {
            (true, true) => BothNearCapResult,
            (true, false) => MysticalNearCapResult,
            (false, true) => BrightNearCapResult,
            _ => NoWarningResult,
        };
    }
}
