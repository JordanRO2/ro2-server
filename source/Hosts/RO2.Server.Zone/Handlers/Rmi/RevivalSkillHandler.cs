using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1684 AnsRevivalSkill (Fixed 13B) - a dead player accepts another player's revival skill.</summary>
/// <remarks>
/// <para>
/// REQUEST, 13 bytes (read_side <c>Blob&lt;13&gt;</c>),
/// <c>[u8 1][f32 x][f32 y][f32 z]</c>. The client sends it from one place only: the confirm dialog that
/// follows a 3016 NfyRevivalSkill offer, which stores <c>{u8 1, x, y, z}</c> with the offer's three
/// floats and sends that record only when the dialog is accepted. So byte 0 is a constant 1, a decline
/// sends nothing, and bytes 1..12 are the position of an offer this server sent: they are
/// byte-equal to bytes 0..11 of a 3016 the same session received.
/// </para>
/// <para>
/// The request is not <c>[u32 casterSessionId][u32 skillId][u8 accept @8]</c>: that "accept" would be
/// the second byte of the y float, so whether a revival happened would depend on the coordinates.
/// </para>
/// <para>
/// THE OFFER. SkillDamageHandler sends the 3016 when a revival skill (Skill_Function 7) lands on a dead PC,
/// and records it on that PC (<see cref="PlayerState.AddRevivalOffer"/>). Every offer raised since the death
/// stays acceptable, including an earlier one when a newer one has been raised. Offers go when the PC is
/// revived (here or by 1372) or changes map (WarpService).
/// </para>
/// <para>
/// THE ANSWER. A 1684 from a dead PC (a state-6 2398 since its last revival, not
/// undone) is answered with 2399 (position form, the 1684's xyz), 2398 state 0, 2497 and a
/// full 2401, in that order, and onlookers get the same run (see
/// <see cref="RevivalSkillPackets.BuildRevivalBurst"/>). The PC comes back with HP and SP both at max, and
/// at the offer's position, not where it fell. So the server moves the PC there too.
/// </para>
/// <para>
/// WHAT IS IGNORED. A 1684 from a PC that was not dead (for example the echo of a second offer after the
/// first 1684 already revived it) gets no 2399, 2398, 2497 or 2401. A 1684 naming no pending offer cannot
/// come from a stock client, and answering it would revive a PC at full HP/SP wherever the request says, so
/// it is ignored. A 1684 while the client loads a map is ignored as well, since a map change drops the
/// offers.
/// </para>
/// </remarks>
public class RevivalSkillHandler : ZoneRmiHandlerBase<RevivalSkillHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.AnsRevivalSkill;
    public override string Name => "RevivalSkill";

    /// <summary>Request size fixed by the client's reader (<c>Blob&lt;13&gt;</c>).</summary>
    private const int RequestLen = 13;

    /// <summary>Byte 0 of every 1684: the confirm dialog writes 1 into its record.</summary>
    private const byte AcceptMarker = 1;

    public RevivalSkillHandler(IServiceScopeFactory s, ILogger<RevivalSkillHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLen)
        {
            Logger.LogDebug("Session {Id}: short 1684 AnsRevivalSkill ({Len} bytes), ignored", session.SessionId, payload.Length);
            return;
        }

        byte accept;
        float x, y, z;
        {
            var reader = new PacketReader(payload.Span);
            accept = reader.ReadByte();   // @0
            x = reader.ReadSingle();      // @1
            y = reader.ReadSingle();      // @5
            z = reader.ReadSingle();      // @9
        }

        if (accept != AcceptMarker)
        {
            Logger.LogDebug("Session {Id}: 1684 with byte 0 = {Accept}, which the client never sends; ignored",
                session.SessionId, accept);
            return;
        }

        if (!player.MapLoaded || player.PendingGliderDismount)
        {
            Logger.LogDebug("{Name} sent a 1684 while loading a map, ignored", player.Name);
            return;
        }

        // Same dead test as RevivalHandler. A living PC gets nothing; any offer
        // left over (one that raced in after the revival) is dropped so it cannot be named after a later death.
        if (!player.IsDead && player.Hp > 0)
        {
            player.ClearRevivalOffers();
            Logger.LogDebug("{Name} sent a 1684 while alive; ignored", player.Name);
            return;
        }

        if (!player.TryTakeRevivalOffer(x, y, z, out var offer))
        {
            Logger.LogDebug("{Name} sent a 1684 at ({X}, {Y}, {Z}) naming none of its {Count} revival offers; ignored",
                player.Name, x, y, z, player.RevivalOfferCount);
            return;
        }

        // Both bars full, at the offered position, and the dead flag cleared, in one step
        // under the vitals lock (ReviveTo). Max(1) only keeps the PC out of the Hp <= 0 dead test; every max this
        // server computes is far above 1.
        player.ReviveTo(Math.Max(1, player.MaxHp), Math.Max(0, player.MaxMp));
        player.X = offer.X;
        player.Y = offer.Y;
        player.Z = offer.Z;

        Logger.LogInformation("Session {Id}: {Name} revived by 0x{Caster:X8}'s skill {SkillId} at ({X}, {Y}, {Z})",
            session.SessionId, player.Name, offer.CasterHandle, offer.SkillId, offer.X, offer.Y, offer.Z);

        // Each frame to the revived PC first, then to the onlookers (BroadcastToNearbyAsync skips the sender),
        // so both see 2399 -> 2398 -> 2497 -> 2401.
        var burst = RevivalSkillPackets.BuildRevivalBurst(
            player.EntityId, offer.X, offer.Y, offer.Z, player.Hp, player.Mp, player.MaxHp, player.MaxMp);
        foreach (var (opcode, body) in burst)
        {
            await session.SendInternalRmiAsync(opcode, body);
            await Broadcast.BroadcastToNearbyAsync(player, opcode, writer => writer.WriteBytes(body));
        }
    }
}
