using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1347 ReqCraftListOpen: the player opens a crafting NPC's window. Answered with S2C 2537 OnAckCraftOpen.
/// </summary>
/// <remarks>
/// <para>
/// The request body is <c>[01 0c][u16 innerSize 12][u16 msgTag 58386][u32 self handle][u32 npc handle]</c>, 14 bytes
/// (<see cref="RequestBodySize"/>); the NPC handle at body+10 (<see cref="NpcHandleOffset"/>) identifies the NPC that
/// owns the window.
/// </para>
/// <para>
/// The answer carries a result code and the NPC handle. <see cref="ResultOpen"/> opens the window. A handle that is not an NPC of this
/// server is refused with <see cref="ResultNpcNotFound"/>, which the client shows as its generic NPC-not-found message
/// and does not open anything. The check uses the server's NPC registry, as RepairItemHandler and
/// GuildStorageOpenHandler do.
/// </para>
/// </remarks>
public class CraftOpenHandler : ZoneRmiHandlerBase<CraftOpenHandler>
{
    /// <summary>msgTag of 2537 OnAckCraftOpen: 0xE413, the request's msgTag 58386 + 1.</summary>
    private const ushort MsgTagAckCraftOpen = 58387;

    /// <summary>Length of the 1347 request body: 2-byte compact prefix, 12-byte inner message.</summary>
    private const int RequestBodySize = 14;

    /// <summary>Offset of the NPC handle in the 1347 body (blob+8 after the 2-byte compact prefix).</summary>
    private const int NpcHandleOffset = 10;

    /// <summary>Result (system message id) that opens the crafting window.</summary>
    internal const short ResultOpen = 0;

    /// <summary>
    /// Result for a request naming a handle that is not an NPC of this server; the client shows its generic
    /// NPC-not-found message (string_ui 2511) and opens nothing.
    /// </summary>
    internal const short ResultNpcNotFound = 20001;

    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftListOpen;
    public override string Name => "CraftOpen";
    public CraftOpenHandler(IServiceScopeFactory s, ILogger<CraftOpenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodySize)
        {
            Logger.LogWarning("{Name}: 1347 body is {Len} bytes (expected {Expected}), no window opened", player.Name, payload.Length, RequestBodySize);
            return;
        }
        uint npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(NpcHandleOffset));

        var npc = (npcHandle & 0xFF) == NpcStateManager.NpcActorTag
            ? services.GetRequiredService<NpcStateManager>().GetNpc(npcHandle)
            : null;
        short result = OpenResult(npcHandle, npc);
        if (result != ResultOpen)
            Logger.LogInformation("{Name} opens crafting at 0x{Npc:X8}, which is no NPC this server spawned: code {Code}",
                player.Name, npcHandle, result);
        else
            Logger.LogDebug("{Name} opens crafting at NPC 0x{Npc:X8} ({NpcId})", player.Name, npcHandle, npc!.NpcId);

        // The answer echoes the requested handle, for both the open and the refused form.
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckCraftOpen,
            writer => writer.WriteBytes(BuildAckCraftOpen2537(player.EntityId, result, npcHandle)));
    }

    /// <summary>
    /// Returns the result for a request naming <paramref name="npcHandle"/>: 0 (open the window) when the handle is an NPC
    /// handle that resolved to <paramref name="npc"/>, otherwise <see cref="ResultNpcNotFound"/>.
    /// </summary>
    internal static short OpenResult(uint npcHandle, NpcRuntimeState? npc) =>
        (npcHandle & 0xFF) != NpcStateManager.NpcActorTag || npc is null ? ResultNpcNotFound : ResultOpen;

    /// <summary>
    /// The 2537 OnAckCraftOpen body: <c>[u16 14][u16 msgTag 58387][u32 self][i16 systemMessageId][u32 npcHandle]</c>, behind a
    /// 1-byte compact prefix. The client reads the i16 first: non-zero is shown as a system message and stops; zero opens
    /// the crafting window on the NPC named by the handle.
    /// </summary>
    internal static byte[] BuildAckCraftOpen2537(uint selfHandle, short systemMessageId, uint npcHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagAckCraftOpen); // blob+2
            p.WriteUInt32(selfHandle);         // blob+4  recipient's own PC handle
            p.WriteInt16(systemMessageId);     // blob+8  0 = open the window, else a result code
            p.WriteUInt32(npcHandle);          // blob+10 the handle from the request
        });
        return pw.ToArray();
    }
}
