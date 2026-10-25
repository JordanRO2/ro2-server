using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1423 Req_CLZSR_UPDATE_KEYCONFIG: saves the player's key bindings.
/// </summary>
/// <remarks>
/// <para>
/// Request layout: <c>[u32 len = 8 + 4·count][u32 pcHandle][u16 mode][u16 count][count × {u8 vk, u8 vk, u16 actionId}]</c>.
/// </para>
/// <para>
/// The server sends no reply. Bindings are stored per character and returned in the world-entry 2558
/// message on login and on glider re-entry, built with <see cref="KeyConfigCodec.BuildAccountConfig2558"/>.
/// </para>
/// </remarks>
public class UpdateKeyConfigHandler : ZoneRmiHandlerBase<UpdateKeyConfigHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.Req_CLZSR_UPDATE_KEYCONFIG;
    public override string Name => "UpdateKeyConfig";

    public UpdateKeyConfigHandler(IServiceScopeFactory s, ILogger<UpdateKeyConfigHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!KeyConfigCodec.TryParseUpdate(payload.Span, out var mode, out var entries))
        {
            Logger.LogWarning("Session {Id}: malformed Req_CLZSR_UPDATE_KEYCONFIG ({Size}B)", session.SessionId, payload.Length);
            return;
        }

        try
        {
            var repo = services.GetRequiredService<RO2.Characters.Domain.IKeyConfigRepository>();
            
            
            
            
            await repo.ApplyUpdateAsync(player.CharacterId, mode, entries, DateTime.UtcNow, ct);
            Logger.LogDebug("{Name}: saved key config (mode {Mode}, {Count} bindings{Kept})", player.Name, mode,
                entries.Length / 4, RO2.Persistence.Mongo.Repositories.KeyConfigRepository.IsFlagOnlyUpdate(mode, entries.Length) ? ", stored list kept" : "");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: could not persist key config", session.SessionId);
        }
    }
}
