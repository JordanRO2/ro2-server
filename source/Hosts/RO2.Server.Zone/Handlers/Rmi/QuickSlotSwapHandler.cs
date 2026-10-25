using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1317 (ReqQuickSlotSwap): swaps the contents of two quick slots.</summary>
/// <remarks>
/// Wire layout: actor handle, followed by two 20-byte slot records (page, slot, entry type, entry id,
/// amount and item uid). The server swaps the two positions it already stores rather than trusting the
/// record contents, which the client may leave stale. The new order is persisted and sent back on login.
/// </remarks>
public class QuickSlotSwapHandler : ZoneRmiHandlerBase<QuickSlotSwapHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqQuickSlotSwap;
    public override string Name => "QuickSlotSwap";

 /// <summary>Expected length of the request body (actor handle and two 20-byte slot records).</summary>
    private const int SwapBlobLen = 48;

    public QuickSlotSwapHandler(IServiceScopeFactory s, ILogger<QuickSlotSwapHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!QuickSlotPackets.TryReadActorHandle(payload.Span, out var actorHandle))
        {
            Logger.LogWarning("Session {Id}: malformed ReqQuickSlotSwap ({Len} bytes)",
                session.SessionId, payload.Length);
            return;
        }

        if (TryReadPositions(payload.Span, out var pageA, out var slotA, out var pageB, out var slotB))
        {
            try
            {
                var repo = services.GetRequiredService<IQuickSlotRepository>();
                await repo.SwapAsync(player.CharacterId, pageA, slotA, pageB, slotB);
            }
            catch (Exception ex)
            {
                
                Logger.LogError(ex, "Session {Id}: could not persist quick slot swap", session.SessionId);
            }

            Logger.LogDebug("{Name} swaps quick slot p{PageA}s{SlotA} <-> p{PageB}s{SlotB}",
                player.Name, pageA, slotA, pageB, slotB);
        }
        else
        {
            Logger.LogWarning("Session {Id}: ReqQuickSlotSwap without its two slot records ({Len} bytes)",
                session.SessionId, payload.Length);
        }

        
        await session.SendRmiAsync(ZoneS2COpcodes.OnSwapQuickSlot,
            writer => writer.WriteBytes(
                QuickSlotPackets.BuildAck(QuickSlotPackets.MsgTagSwapQuickSlot, actorHandle)));
    }

 /// <summary>Reads the page and slot of both slot records.</summary>
    private static bool TryReadPositions(ReadOnlySpan<byte> body, out byte pageA, out byte slotA,
        out byte pageB, out byte slotB)
    {
        pageA = slotA = pageB = slotB = 0;
        
        
        if (!body.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob)) return false;
        if (blob.Length < SwapBlobLen || BinaryPrimitives.ReadUInt16LittleEndian(blob) < SwapBlobLen)
            return false;
        pageA = blob[8];
        slotA = blob[9];
        pageB = blob[28];
        slotB = blob[29];
        return true;
    }
}
