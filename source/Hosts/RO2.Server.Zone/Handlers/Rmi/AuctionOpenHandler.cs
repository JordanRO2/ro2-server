using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1399 ReqAuctionOpen - Open auction house</summary>
public class AuctionOpenHandler : ZoneRmiHandlerBase<AuctionOpenHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAuctionOpen;
    public override string Name => "AuctionOpen";
    public AuctionOpenHandler(IServiceScopeFactory s, ILogger<AuctionOpenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>1399 msgTag (blob+2): 58206. The request is blob 12: <c>[u16 12][u16 tag][u32 PC handle @4]
    /// [u32 auction NPC handle @8, low byte 0x13]</c>.</summary>
    internal const ushort MsgTagReqOpen = 0xE35E;

    /// <summary>Size of the 1399 request blob: msgTag @2, PC handle @4, auction NPC handle @8.</summary>
    private const int ReqOpenBlobSize = 12;

    /// <summary>Offset of the msgTag within the 1399 request blob.</summary>
    private const int ReqOpenTagOffset = 2;

    /// <summary>Offset of the auction NPC handle within the 1399 request blob.</summary>
    private const int ReqOpenNpcHandleOffset = 8;

    /// <summary>2454 msgTag (compact-length envelope): 58207.</summary>
    internal const ushort MsgTagOnOpen = 0xE35F;

    /// <summary>2454 result that opens the auction window (83); any other value closes it.</summary>
    internal const ushort ResultOpenOk = 0;

    /// <summary>
    /// 2454 result of an open that fails the NPC gate. The client reads only the i16 at blob+8: 0
    /// opens window 83, anything else closes it and shows <c>Auction_ShowResultMessageByCode</c>. The code is
    /// chosen by this server for an unresolved / distant / cross-map auction NPC: 16301 is a code that
    /// function already renders.
    /// </summary>
    internal const ushort ResultOpenRefused = AuctionPackets.ResultSellInternalError;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // E-H8: the 1399 NPC handle is resolved and the player must be alive, on its map and within reach.
        // An unparseable request or an unresolved handle is refused the same way (never left unanswered).
        NpcRuntimeState? npc = null;
        if (TryParseNpcHandle(payload.Span, out var npcHandle) && (npcHandle & 0xFF) == NpcStateManager.NpcActorTag)
            npc = services.GetRequiredService<NpcStateManager>().GetNpc(npcHandle);
        var gate = ActorGate.CanTransact(player, npc, ActorGate.NpcInteractReach);
        if (gate != ActorRefusal.None)
        {
            Logger.LogInformation("{Name} opens auction house at 0x{Npc:X8}: refused ({Gate}), code {Code}",
                player.Name, npcHandle, gate, ResultOpenRefused);
            await SendOpenAsync(session, player, ResultOpenRefused);
            return;
        }

        Logger.LogDebug("{Name} opens auction house", player.Name);
        await SendOpenAsync(session, player, ResultOpenOk);
    }

    /// <summary>The auction NPC handle of a 1399 (blob+8), false when the request is not a 1399 blob of 12.</summary>
    internal static bool TryParseNpcHandle(ReadOnlySpan<byte> payload, out uint npcHandle)
    {
        npcHandle = 0;
        if (!MailPackets.TryGetBlob(payload, out var b) || b.Length < ReqOpenBlobSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(ReqOpenTagOffset)) != MsgTagReqOpen) return false;
        npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(ReqOpenNpcHandleOffset));
        return true;
    }

    private static Task SendOpenAsync(ProudSession session, PlayerState player, ushort result)
    {
        // OnAuctionOpen (2454): compact-len BA body. An empty house is compactLen "01 0A",
        // selfLen 10, msgTag 58207, u32 auctionId, u16 result. auctionId = a stable non-zero id for this
        // player's auction session; a generated uint is fine - the client only needs well-formed
        // framing here.
        uint auctionId = MakeAuctionSessionId(player);
        return session.SendRmiAsync(ZoneS2COpcodes.OnAuctionOpen, writer =>
        {
            writer.WriteCompactBABody(p =>
            {
                p.WriteUInt16(MsgTagOnOpen);
                p.WriteUInt32(auctionId);
                p.WriteUInt16(result);
            });
        });
    }

    // Stable non-zero session id derived from the player (never 0). Not a real object id yet.
    private static uint MakeAuctionSessionId(PlayerState player)
    {
        uint id = player.CharacterId != 0 ? player.CharacterId : player.SessionId;
        return id == 0 ? 1u : id;
    }
}
