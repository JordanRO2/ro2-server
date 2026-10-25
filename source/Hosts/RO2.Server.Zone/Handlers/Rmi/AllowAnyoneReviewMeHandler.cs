using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1697 ReqAllowAnyoneReviewMe, which sets whether other players may review this player.
/// </summary>
/// <remarks>
/// The request is one byte: 0 or 1. The reply (3036 AnsAllowAnyoneReviewMe) is <c>[u32 pcHandle][u8 allow]</c>
/// and is sent to nearby players as well as the requester. The setting is stored on the character and persists
/// across sessions.
/// </remarks>
public class AllowAnyoneReviewMeHandler : ZoneRmiHandlerBase<AllowAnyoneReviewMeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAllowAnyoneReviewMe;
    public override string Name => "AllowAnyoneReviewMe";

    public AllowAnyoneReviewMeHandler(IServiceScopeFactory s, ILogger<AllowAnyoneReviewMeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc)
    {
        
        
        ReviewPermission.Bind(pm);
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 1)
        {
            Logger.LogWarning("{Name}: empty 1697 body (retail is 1 byte)", player.Name);
            return;
        }
        bool allow = payload.Span[0] != 0;   

        
        
        
        if (player.Character is { } snapshot)
            snapshot.AllowAnyoneReview = allow;

        Logger.LogDebug("{Name} sets allow-anyone-review = {Allow}", player.Name, allow);

        byte flag = allow ? (byte)1 : (byte)0;
        void Write(PacketWriter w)
        {
            w.WriteUInt32(player.EntityId); 
            w.WriteByte(flag);              
        }

        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsAllowAnyoneReviewMe, Write);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.AnsAllowAnyoneReviewMe, Write);

        
        
        
        try
        {
            await services.GetRequiredService<ICharacterRepository>()
                .UpdateAllowAnyoneReviewAsync(player.CharacterId, allow, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Name}: could not persist allow-anyone-review = {Allow}", player.Name, allow);
        }
    }
}

/// <summary>
/// Per-character "anyone may review me" setting, written by <see cref="AllowAnyoneReviewMeHandler"/> and read
/// by the review handler (<see cref="ReviewPCHandler"/>).
/// </summary>
/// <remarks>
/// The default is allowed. The value is read from the online character's snapshot. Targets without an online
/// snapshot are answered as unknown players before this check is reached.
/// </remarks>
internal static class ReviewPermission
{
    private static PlayerStateManager? _players;

 /// <summary>Binds the player state manager. Called once by the handler constructor at startup.</summary>
    public static void Bind(PlayerStateManager players) => _players = players;

    public static bool IsAllowed(uint characterId) =>
        _players?.GetPlayerByCharacterId(characterId)?.Character?.AllowAnyoneReview ?? true;
}
