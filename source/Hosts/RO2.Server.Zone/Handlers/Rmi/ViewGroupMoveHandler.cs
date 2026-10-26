using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1526 ViewGroupPCMoveTo - The PRIMARY movement packet.
/// This is used instead of RMI 1305 MovePC for actual player movement.
///
/// Wire format: the CLIENT serializes 93 bytes (the 2750 layout), and the transport hands
/// this handler 97: the 93 plus 4 trailing bytes the ProudNet RC4 layer leaves on the plaintext (CRC32 the server does
/// not verify; see <see cref="CanonicalPayloadLength"/>). The S2C 2750 body is the 93.
/// Layout:
///   +0  uint32  session_entity_id
///   +4  36 bytes zeros/flags (movement state, animation, etc)
///   +40 float   current_x  (at hex offset 0x28)
///   +44 float   current_y
///   +48 float   current_z
///   +52 float   dest_x
///   +56 float   dest_y
///   +60 float   dest_z
///   +64 u32
///   +68 float   FACING (yaw, radians), stored as PlayerState.FacingYaw (see TrackFacing)
///   +72 ...     the rest (+76 f32 speed, +80 u8, +81 u32, 8 bytes)
/// </summary>
public class ViewGroupMoveHandler : ZoneRmiHandlerBase<ViewGroupMoveHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ViewGroupPCMoveTo;
    public override string Name => "ViewGroupMove";

    public ViewGroupMoveHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<ViewGroupMoveHandler> logger,
        PlayerStateManager playerManager,
        BroadcastService broadcast)
        : base(scopeFactory, logger, playerManager, broadcast)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        // Only the canonical frame is a movement report. Any other length is malformed (or a different transport
        // path, see CanonicalPayloadLength): relaying it would send a mis-sized 2750 to the whole map, which crashes or
        // culls clients, so it is dropped and the last good position/pin is kept.
        if (!IsCanonicalLength(payload.Length)) return;

        // A 1526 that arrives before the client acked its map (1304) or inside a glider re-entry window is the OLD
        // map's position: taking it would overwrite the new map's spawn. Dropped; nothing is stored or relayed.
        if (!AcceptsMoves(player)) return;

        var reader = new PacketReader(payload.Span);

        // Parse entity ID
        var entityId = reader.ReadUInt32();

        // Skip movement state bytes (flags/animation data)
        reader.Skip(MovementStateLength);

        // Current position (floats at payload offset CurrentPositionOffset, +40)
        var curX = reader.ReadSingle();
        var curY = reader.ReadSingle();
        var curZ = reader.ReadSingle();

        // Destination position
        var destX = reader.ReadSingle();
        var destY = reader.ReadSingle();
        var destZ = reader.ReadSingle();

        // A DEAD PC's 1526s neither move it nor all reach the onlookers. The corpse is pinned at the
        // point the 1679 reported (UpdateDeadPointHandler), and a dead client keeps sending 1526s from
        // positions that slide off it. Between a death's 1679 and its revival (the next 1372 / 1684 / self 2399), a
        // 1526 whose current x/y are bit-equal to the 1679 point is relayed (and comes back as the self 2750 echo);
        // one whose x/y differ is not. A z drift of up to 0.01 at the pin is still relayed, so only x/y
        // are compared. The position held for the PC stays the pin, so these positions are not taken either.
        // The rule is "relay only at the pin", never "always relay at the pin". Without this the corpse slid on every
        // onlooker's screen, and the 2504 a newcomer gets and the saved position drifted off the spot.
        if (player.IsDead)
        {
            player.LastActivity = DateTime.UtcNow;
            if (!IsAtDeadPoint(player, curX, curY))
                return;
            var pinned = BuildMoveBody(player.EntityId, payload.Span);
            // Same no-self-echo policy as the live path below.
            await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.ViewGroupPCMoveTo,
                writer => writer.WriteBytes(pinned));
            return;
        }

        // Validate the client's reported CURRENT position (+40) BEFORE taking it as the authoritative
        // server-side location. (1526 also carries a destination at +52; we keep that only for the broadcast.)
        // X@payload+40, Y@+44, Z@+48 change as the avatar walks. A teleport /
        // speedhack over the measured cap (MovementValidator, speed from the server's live ride/effect state,
        // NOT a stored 2497) is refused here: the position is not taken and NOT rebroadcast, so this PC — and
        // the combat reach gate that reads its position — keeps the last valid spot.
        var lastX = player.X; // the previous accepted point, half of the channel-move test below
        var lastY = player.Y;
        var speed = ActorMoveSpeed.CurrentSpeedOf(session, player, services.GetRequiredService<GameDataStore>());
        var guard = services.GetRequiredService<MovementGuard>();
        var refusal = guard.ValidateClientMove(player, new MovePos(curX, curY, curZ), speed);
        if (refusal != MoveRefusal.None)
        {
            player.LastActivity = DateTime.UtcNow;
            Logger.LogDebug("Player {Name} (eid 0x{Eid:X8}) move refused ({Refusal}): claimed ({CX:F1},{CY:F1},{CZ:F1}); kept ({PX:F1},{PY:F1})",
                player.Name, player.EntityId, refusal, curX, curY, curZ, player.X, player.Y);
            return;
        }

        // Accepted: player.X/Y/Z now hold (curX, curY, curZ). A move ends an immovable channel
        // (SkillChannelingPackets.CancelsOnMove), decided against the previous accepted point captured above.
        var channelToCancel = player.ActiveChannelSkillId;
        if (channelToCancel == 0
            || !DisplacesChannel(lastX, lastY, curX, curY, destX, destY)
            || !SkillChannelingPackets.CancelsOnMove(channelToCancel)
            || !player.TryEndChannel(channelToCancel))
            channelToCancel = 0;

        TrackFacing(player, payload.Span);
        // Throttle: 1526 fires ~922x/session. Only log on the idle->moving transition, not every tick,
        // to avoid flooding the zone log.
        // The 1526 carries the destination (+52) as well as the current position (+40). When they
        // coincide the client has ARRIVED and is standing still; only when they differ is it walking.
        // Tracking this accurately matters for combat: the attack-start grant (2694) must reach the
        // attacker only once he is settled, never mid-approach (see NormalAttackStartHandler).
        var wasMoving = player.IsMoving;
        var arrived = MathF.Abs(destX - curX) < ChannelMoveEpsilon && MathF.Abs(destY - curY) < ChannelMoveEpsilon;
        player.IsMoving = !arrived;
        player.LastActivity = DateTime.UtcNow;

        if (!wasMoving)
            Logger.LogDebug("Player {Name} (eid 0x{Eid:X8}) started moving: pos=({CX:F1},{CY:F1},{CZ:F1}) dest=({DX:F1},{DY:F1},{DZ:F1})",
                player.Name, player.EntityId, curX, curY, curZ, destX, destY, destZ);

        // 2750 ViewGroupPCMoveTo S2C body = [u32 actorHandle][1526_payload[4..^4]] = 93 bytes.
        // The S2C body is 4 bytes SHORTER than the inbound C2S 1526 payload (it ends at the constant trailer
        // `05 dd 01`). The C2S 1526 carries 4 extra TRAILING bytes (client input seq/stamp) that are not relayed
        // in the S2C. Forwarding payload[4..] makes the 2750 97B -> the client mis-parses the move packet and
        // CULLS the remote actor (avatar vanishes as soon as it moves).
        var body = BuildMoveBody(player.EntityId, payload.Span);

        // NO self-echo. The client's 2750 handler wraps its entire body in
        // `if (bodyHandle != localPlayerHandle)`, so a 2750 carrying the
        // receiver's OWN handle is a no-op on the local avatar. Echoing to the mover would only waste a packet per move.
        // The mover's own movement and
        // its run/idle animation are entirely client-side prediction; the server cannot drive them.
        //
        // Relay the body to OTHER players on the map (multiplayer sync). The handle MUST be
        // the mover's EntityId (the id other clients know this actor by from OnPC_In 2504) - NOT
        // SessionId - or the move can't bind to the spawned avatar.
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.ViewGroupPCMoveTo,
            writer => writer.WriteBytes(body));

        if (channelToCancel != 0)
        {
            // The server ends the channel (see SkillChannelingPackets.CancelsOnMove): 2585
            // OnSkillChannelingCancelNotify (COMPACT, msgTag 58288, [u32 0][u32 casterHandle][u32 skillId]) to
            // the whole map with NO exclusion. The caster's own client must get it too: it ends the cast locally.
            // The channel is already over here
            // (TryEndChannel above), so the 1609 the client may still send gets 2897 result 1 and no 2586.
            Logger.LogDebug("{Name} moved during channel {SkillId}; channel cancelled", player.Name, channelToCancel);
            var cancel = CombatSkillNotifyPackets.BuildSkillChannelingCancelNotify2585(player.EntityId, channelToCancel);
            await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnSkillChannelingCancelNotify,
                writer => writer.WriteBytes(cancel));
        }
    }

    /// <summary>
    /// The 1526 payload length this handler is given: 93 bytes the client writes + 4 trailing transport bytes.
    /// CONFIRMED for the live RC4 fast-mode path only: BuildMoveBody's 2750 (= handle + payload[4..^4]) is 93 bytes
    /// iff the payload is 97 (ViewGroupMove2750Tests); the client writer emits exactly 93.
    /// The AES path (flags1 0x01) hands the handler the whole decrypted block (pad + CRC32
    /// trailer), whose length is not 97, so a client sending 1526 over AES would
    /// have its moves dropped here (keep-last-good) rather than relayed mis-sized.
    /// </summary>
    internal const int CanonicalPayloadLength = ClientBodyLength + TransportTrailerLength;

    /// <summary>Length of the body the client serializes for a 1526, which is also the 2750 body: 93 bytes.</summary>
    internal const int ClientBodyLength = 93;

    /// <summary>Trailing bytes the transport leaves after the client body in the 1526 payload; never relayed.</summary>
    internal const int TransportTrailerLength = 4;

    /// <summary>Length of the leading u32 entity/actor handle in both the 1526 payload and the 2750 body.</summary>
    internal const int ActorHandleLength = 4;

    /// <summary>Length of the movement state / animation flags that follow the entity id (payload +4..+39).</summary>
    internal const int MovementStateLength = 36;

    /// <summary>Offset of the current-position floats (x, y, z) in the 1526 payload: entity id plus movement state (+40).</summary>
    internal const int CurrentPositionOffset = ActorHandleLength + MovementStateLength;

    /// <summary>True for the one inbound length that maps to the 93-byte 2750 body (see <see cref="CanonicalPayloadLength"/>).</summary>
    internal static bool IsCanonicalLength(int payloadLength) => payloadLength == CanonicalPayloadLength;

    /// <summary>
    /// False while the PC has not acked its current map (<see cref="PlayerState.MapLoaded"/>, 1304) or is inside a glider
    /// re-entry (<see cref="PlayerState.PendingGliderDismount"/>): a 1526 then carries the previous map's coordinates.
    /// </summary>
    internal static bool AcceptsMoves(PlayerState player) => player.MapLoaded && !player.PendingGliderDismount;

    /// <summary>Offset of the facing f32 (yaw, radians) in the 1526 payload, the same offset in the 2750 relay.</summary>
    internal const int FacingYawOffset = 68;

    /// <summary>
    /// Keeps the PC's facing: the f32 at payload+68 into <see cref="PlayerState.FacingYaw"/> (persisted) and into the
    /// world-entry snapshot, which the 2310-family heads are written from (CharacterHeadRules.FacingByte, head+107).
    /// A short payload or a non-finite value changes nothing.
    /// </summary>
    /// <remarks>
    /// The head's [107] byte is within one 2-degree step of this f32 of the session's last C2S 1526, and a 2310
    /// matches the first 1526 after it (the client spawns facing that way). The 2750 relay's +68
    /// lies in [-3.14, 3.71] rad, i.e. radians. Dead PCs' 1526s are not taken (see
    /// HandleAsync), like their positions.
    /// </remarks>
    internal static void TrackFacing(PlayerState player, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FacingYawOffset + sizeof(float))
            return;
        var yaw = BinaryPrimitives.ReadSingleLittleEndian(payload[FacingYawOffset..]);
        if (!float.IsFinite(yaw))
            return;
        player.FacingYaw = yaw;
        if (player.Character is { } snapshot)
            snapshot.FacingYaw = yaw;
    }

    /// <summary>Displacement (x/y, per axis) below which a 1526 is not a move; the same 0.1 the arrival test uses.</summary>
    internal const float ChannelMoveEpsilon = 0.1f;

    /// <summary>
    /// True when a 1526 displaces the PC: its destination differs from its current point, or its current point
    /// from the last one the server held. A 1526 that only turns the avatar (destination == current == last
    /// point) is not a move, so it must not cancel a channel.
    /// </summary>
    internal static bool DisplacesChannel(float lastX, float lastY, float curX, float curY, float destX, float destY)
        => MathF.Abs(destX - curX) >= ChannelMoveEpsilon || MathF.Abs(destY - curY) >= ChannelMoveEpsilon
           || MathF.Abs(curX - lastX) >= ChannelMoveEpsilon || MathF.Abs(curY - lastY) >= ChannelMoveEpsilon;

    /// <summary>
    /// True when a dead PC's 1526 reports the point it is pinned at: its current x/y (payload +40/+44)
    /// bit-equal to <see cref="PlayerState.X"/>/<see cref="PlayerState.Y"/>, which the 1679 set
    /// (UpdateDeadPointHandler). Bit equality: the at-pin 1526s carry the 1679's
    /// floats verbatim, and the off-pin ones differ from them.
    /// </summary>
    internal static bool IsAtDeadPoint(PlayerState player, float curX, float curY)
        => BitConverter.SingleToInt32Bits(curX) == BitConverter.SingleToInt32Bits(player.X)
           && BitConverter.SingleToInt32Bits(curY) == BitConverter.SingleToInt32Bits(player.Y);

    /// <summary>
    /// Builds the S2C 2750 ViewGroupPCMoveTo body from an inbound C2S 1526 payload:
    /// [u32 actorHandle][inbound[4..^4]] = 93 bytes. The 4 trailing bytes of the C2S payload are a
    /// client input seq/stamp that is not relayed. The caller has checked
    /// <see cref="IsCanonicalLength"/>.
    /// </summary>
    internal static byte[] BuildMoveBody(uint actorHandle, ReadOnlySpan<byte> inbound)
    {
        var body = new byte[ActorHandleLength + inbound.Length - ActorHandleLength - TransportTrailerLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, actorHandle);
        inbound[ActorHandleLength..^TransportTrailerLength].CopyTo(body.AsSpan(ActorHandleLength));
        return body;
    }
}
