using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1540 ReqSaveQuestTraceList. Saves the client's quest tracking list.
/// </summary>
public class SaveQuestTraceHandler : ZoneRmiHandlerBase<SaveQuestTraceHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSaveQuestTraceList;
    public override string Name => "SaveQuestTrace";

    public SaveQuestTraceHandler(IServiceScopeFactory s, ILogger<SaveQuestTraceHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogTrace("Session {Id}: SaveQuestTrace ({Size}b)", session.SessionId, payload.Length);

        
        var span = payload.Span;
        if (span.Length < 6) return;
        ushort count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
        var ids = new HashSet<uint>();
        for (int i = 0; i < count; i++)
        {
            int off = 6 + 4 * i;
            if (off + 4 > span.Length) break;
            ids.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)));
        }
        ids.RemoveWhere(q => !player.ActiveQuests.Contains(q));   
        player.TracedQuests = ids;
        await services.GetRequiredService<RO2.Application.Interfaces.IQuestRepository>()
            .SetQuestsTracedAsync(player.CharacterId, ids);
        Logger.LogDebug("Session {Id}: saved {N} traced quests", session.SessionId, ids.Count);
    }
}
