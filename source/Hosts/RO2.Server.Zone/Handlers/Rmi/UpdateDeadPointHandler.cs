using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1679 ReqUpdateDeadPoint: the client reports the point where its character died. The server pins
/// the corpse at that point until revival.
/// </summary>
/// <remarks>
/// <para>
/// Request layout: <c>[8 bytes][f32 x][f32 y][f32 z][1 byte]</c>, 21 bytes in total. Only the three floats
/// carry data.
/// </para>
/// <para>
/// The client sends this in answer to the victim's own 2391 OnDeadMe. No reply is sent.
/// </para>
/// <para>
/// The reported point can differ from the server's own death position, and later view updates for the dead
/// character carry the reported point. The stored point is therefore the one other players see. Revival
/// moves the character off the pin: a save-point revival warps to the save point, and a skill revival goes
/// to the caster's offer.
/// </para>
/// <para>
/// Applies only to a dead character, and the map is unchanged. A point reported for a live character is
/// ignored. No bound is applied to the point.
/// </para>
/// </remarks>
public class UpdateDeadPointHandler : ZoneRmiHandlerBase<UpdateDeadPointHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqUpdateDeadPoint;
    public override string Name => "UpdateDeadPoint";

 /// <summary>Expected length of the 1679 request body, in bytes.</summary>
    internal const int BodyLength = 21;

 /// <summary>Offset of the x coordinate; y and z follow it.</summary>
    internal const int PointOffset = 8;

    public UpdateDeadPointHandler(IServiceScopeFactory s, ILogger<UpdateDeadPointHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength) return Task.CompletedTask;
        var reader = new PacketReader(payload.Span);
        reader.Skip(PointOffset); 
        var posX = reader.ReadSingle();
        var posY = reader.ReadSingle();
        var posZ = reader.ReadSingle();

        if (TryPinDeadPoint(player, posX, posY, posZ))
            Logger.LogDebug("{Name} pinned at dead point ({X}, {Y}, {Z}) on map {MapId}",
                player.Name, posX, posY, posZ, player.MapId);
        else
            Logger.LogDebug("{Name} reported dead point ({X}, {Y}, {Z}) while dead={Dead}; ignored",
                player.Name, posX, posY, posZ, player.IsDead);
        return Task.CompletedTask;
    }

 /// <summary>
 /// Moves a dead character to the point its 1679 reported. Returns false, changing nothing, for a live
 /// character or for a point that is not three finite floats.
 /// </summary>
    internal static bool TryPinDeadPoint(PlayerState player, float x, float y, float z)
    {
        if (!player.IsDead) return false;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        player.X = x;
        player.Y = y;
        player.Z = z;
        return true;
    }
}
