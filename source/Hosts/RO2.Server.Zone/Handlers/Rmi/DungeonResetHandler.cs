using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1534 ReqDungeonReset, which releases the character's binding to its party's open dungeon instances.</summary>
/// <remarks>
/// For each instance released, AnsDungeonReset (2771) is sent with its DungeonID. A request with nothing to release
/// gets no reply. An instance with no members closes on its own after a period, and a cleared instance closes after
/// a grace period.
/// </remarks>
public class DungeonResetHandler : ZoneRmiHandlerBase<DungeonResetHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqDungeonReset;
    public override string Name => "DungeonReset";

    public DungeonResetHandler(IServiceScopeFactory s, ILogger<DungeonResetHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var party = services.GetRequiredService<PartyService>().GetPlayerParty(player.SessionId);
        var owner = DungeonInstanceOwner.For(party, player.CharacterId);
        var reset = DungeonInstanceRegistry.Shared.Reset(owner, player.MapId, player.CharacterId);
        if (reset.Count == 0)
        {
            Logger.LogDebug("{Name} requested a dungeon reset on map {Map}: {Owner} has no instance it is bound to, or it stands in one; no 2771",
                player.Name, player.MapId, owner);
            return;
        }

        foreach (var dungeonId in reset)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsDungeonReset, Build2771(dungeonId));
        Logger.LogInformation("{Name} (character {Character}) released its binding to {Owner}'s dungeon instance(s) [{Ids}]",
            player.Name, player.CharacterId, owner, string.Join(", ", reset));
    }

 /// <summary>Builds the 4-byte AnsDungeonReset body.</summary>
 /// <param name="dungeonId">The DungeonID of the released instance.</param>
    internal static byte[] Build2771(uint dungeonId)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, dungeonId);
        return body;
    }
}
