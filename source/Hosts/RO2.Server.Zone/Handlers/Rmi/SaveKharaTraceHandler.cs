using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1434 ReqSaveKharaTraceList. Persists the character's tracker-pinned Khara list,
/// which RMI 1435 ReqLoadKharaTraceList returns in 2780.
/// </summary>
/// <remarks>
/// The list is stored per character, keyed by the character UID of the session.
/// The client sends it after 1540 ReqSaveQuestTraceList and before a map change, logout or character change,
/// while the player state is still live.
/// </remarks>
public class SaveKharaTraceHandler : ZoneRmiHandlerBase<SaveKharaTraceHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSaveKharaTraceList;
    public override string Name => "SaveKharaTrace";

    public SaveKharaTraceHandler(IServiceScopeFactory s, ILogger<SaveKharaTraceHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogTrace("Session {Id}: SaveKharaTrace ({Size}b)", session.SessionId, payload.Length);

        
        
        
        var span = payload.Span;
        if (span.Length < 6) return;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(span);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
        if (len != 2u + 4u * count || span.Length < 6 + 4 * count)
        {
            Logger.LogDebug("Session {Id}: malformed 1434 (len={Len}, count={Count}, {Size}b) - ignored",
                session.SessionId, len, count, span.Length);
            return;
        }

        
        var ids = new List<uint>(count);
        for (int i = 0; i < count; i++)
            ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(6 + 4 * i)));

        await services.GetRequiredService<RO2.Application.Interfaces.IKharaTraceRepository>()
            .SaveAsync(player.CharacterId, ids, ct);
        Logger.LogDebug("Session {Id}: saved {N} traced Khara for character {Char}",
            session.SessionId, ids.Count, player.CharacterId);
    }
}
