using RO2.Domain.Entities;
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles Req_CLZSR_INIT_KEYCONFIG: the player resets the key settings to default.</summary>
/// <remarks>
/// <para>
/// The request body is 6 bytes: <c>[u32 sender actor handle][u16 mode]</c>. A mode of 0 resets the binding set that the server does not store; any other mode resets the stored set.
/// </para>
/// <para>
/// The server keeps one key binding list and sends it back at every world entry (2558). A reset of the stored set empties that list and keeps the stored mode. A character with no stored list has nothing to clear. No reply is sent.
/// </para>
/// </remarks>
public class InitKeyConfigHandler : ZoneRmiHandlerBase<InitKeyConfigHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.Req_CLZSR_INIT_KEYCONFIG;
    public override string Name => "InitKeyConfig";

 /// <summary>
 /// Expected request body length, in bytes.
 /// </summary>
    internal const int RequestLength = 6;

    public InitKeyConfigHandler(IServiceScopeFactory s, ILogger<InitKeyConfigHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Reads the u16 mode at body offset 4. Returns false when the body is shorter than the 6 bytes sent.
 /// </summary>
    internal static bool TryParseMode(ReadOnlySpan<byte> body, out ushort mode)
    {
        mode = 0;
        if (body.Length < RequestLength) return false;
        mode = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4));
        return true;
    }

 /// <summary>
 /// Returns true when the reset clears the stored binding set, which is any non-zero mode.
 /// </summary>
    internal static bool ClearsStoredSet(ushort mode) => mode != 0;

 /// <summary>
 /// Returns the document to store after the stored set is reset: the same character and mode with no bindings, or null when there is nothing to change.
 /// </summary>
    internal static KeyConfigDocument? AfterReset(KeyConfigDocument? stored, DateTime updatedUtc)
    {
        if (stored is null || stored.Entries.Length == 0) return null;
        return new KeyConfigDocument
        {
            Id = stored.Id,
            Mode = stored.Mode,
            Entries = Array.Empty<byte>(),
            UpdatedUtc = updatedUtc,
        };
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParseMode(payload.Span, out var mode))
        {
            Logger.LogWarning("Session {Id}: Req_CLZSR_INIT_KEYCONFIG of {Size}B, not the 6 the client sends",
                session.SessionId, payload.Length);
            return;
        }

        if (!ClearsStoredSet(mode))
        {
            Logger.LogDebug("{Name}: key settings reset for mode 0 (binding set 0, not stored here)", player.Name);
            return;
        }

        try
        {
            var repo = services.GetRequiredService<RO2.Characters.Domain.IKeyConfigRepository>();
            var reset = AfterReset(await repo.GetAsync(player.CharacterId, ct), DateTime.UtcNow);
            if (reset is not null)
                await repo.SaveAsync(reset, ct);
            Logger.LogDebug("{Name}: key settings reset for mode {Mode}{Cleared}", player.Name, mode,
                reset is not null ? ", stored bindings cleared" : ", nothing stored to clear");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: could not clear the stored key config", session.SessionId);
        }
    }
}
