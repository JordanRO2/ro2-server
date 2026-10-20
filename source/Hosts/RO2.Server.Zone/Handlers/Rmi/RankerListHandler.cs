using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1656 (ReqRankerList): when a player uses a ranking board NPC, replies with RMI 2966
/// (AnsRankerList), the ranker that the board displays, including the full equipment needed to dress the model.
/// </summary>
/// <remarks>
/// <para>Request, 5 bytes: ranking category (u8 at offset 0) and the board NPC's actor handle (u32 at offset 1,
/// unaligned). The category is the Ranking_Type of the ranking data (2, 3, 4, 11 and 12 have a board; the client
/// also asks for 1).</para>
/// <para>Reply: <c>[u32 blobLength][blob][u32 boardNpcHandle][u16 result]</c>. The handle is echoed so the client
/// binds the answer to the board. The result is 0 when a ranker is returned and <see cref="EmptyBoardResult"/>
/// when the board is empty.</para>
/// <para>Blob: <c>[u32 recordCount]</c> followed by the records,
/// <c>[u64 characterGid][byte[20] rankerHeader][u16 nameChars][wchar name][u16 equipCount][byte[28] equip x equipCount]</c>.
/// A board shows one ranker or none. This server keeps no ranking standings, so it always answers with the
/// empty-board form.</para>
/// </remarks>
public class RankerListHandler : ZoneRmiHandlerBase<RankerListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRankerList;
    public override string Name => "RankerList";

    /// <summary>Request length: <c>[u8 category][u32 boardNpcHandle]</c>.</summary>
    private const int RequestLength = 5;

    /// <summary>Request offset of the board NPC's actor handle, after the category byte.</summary>
    private const int BoardHandleOffset = 1;

    /// <summary>Trailing result word (31201) of the reply when the board has no ranker to show. A returned ranker carries 0.</summary>
    private const ushort EmptyBoardResult = 0x79E1;

    /// <summary>Width of the ranker header inside a record.</summary>
    private const int RankerHeaderLength = 20;

    public RankerListHandler(IServiceScopeFactory s, ILogger<RankerListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength) return;

        var span = payload.Span;
        byte category = span[0];
        uint boardNpcHandle = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[BoardHandleOffset..RequestLength]);

        Logger.LogDebug("[RankerList] {Name} asked board {Handle:X8} for category {Category}",
            player.Name, boardNpcHandle, category);

        // No ranking standings are kept (the ranking data only defines the boards), so there is no ranker to
        // describe: the answer is the empty-board form.
        byte[] blob = BuildEmptyRankerListBlob();

        await session.SendRmiAsync(ZoneS2COpcodes.AnsRankerList, writer =>
        {
            writer.WriteUInt32((uint)blob.Length);
            writer.WriteBytes(blob);
            writer.WriteUInt32(boardNpcHandle);   // echoed so the client binds the answer to the board
            writer.WriteUInt16(EmptyBoardResult); // 0 is the populated form
        }, ct);
    }

    /// <summary>
    /// Builds the reply blob used when the board has no ranker to show: a record count of 0 followed by one
    /// unused record slot, zero-filled (36 bytes in all). The slot is a record with no name and no equipment:
    /// <c>[u64 characterGid = 0][byte[20] rankerHeader = 0][u16 nameChars = 0][u16 equipCount = 0]</c>.
    /// </summary>
    internal static byte[] BuildEmptyRankerListBlob()
    {
        using var w = new PacketWriter();

        w.WriteUInt32(0);                     // recordCount: this board has no ranker
        w.WriteUInt64(0);                     // unused slot: characterGid
        w.WriteZeros(RankerHeaderLength);     // unused slot: rankerHeader
        w.WriteUInt16(0);                     // unused slot: nameChars
        w.WriteUInt16(0);                     // unused slot: equipCount

        return w.GetWrittenSpan().ToArray();
    }
}
