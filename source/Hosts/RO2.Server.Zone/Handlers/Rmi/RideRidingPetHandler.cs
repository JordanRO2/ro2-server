using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1510 (ReqRideRidingPet). Mounts a riding vehicle, or dismounts when the player is already riding
/// it. The reply is 2719 AnsRideRidingPet.
/// </summary>
/// <remarks>
/// The request body is <c>[u32 vehicleId][u32 skillId]</c> followed by padding. Requesting the vehicle that is
/// already ridden drops the ride. Requesting another vehicle while riding also drops the ride, without mounting
/// the requested one. The current ride is tracked by <see cref="RidingService"/>.
/// </remarks>
public class RideRidingPetHandler : ZoneRmiHandlerBase<RideRidingPetHandler>
{
 /// <summary>2719 result of a successful mount.</summary>
    internal const ushort ResultMounted = 0;

 /// <summary>2719 result for a request that dismounted the player from the requested vehicle.</summary>
    internal const ushort ResultDismounted = 21801; 

 /// <summary>2719 result for a request naming another vehicle while riding. The ride is dropped and nothing is mounted.</summary>
    internal const ushort ResultSwitchDropped = 17;

 /// <summary>C2S 1510 body length.</summary>
    private const int RequestLen = 24;

    public override ushort Opcode => ZoneC2SOpcodes.ReqRideRidingPet;
    public override string Name => "RideRidingPet";
    public RideRidingPetHandler(IServiceScopeFactory s, ILogger<RideRidingPetHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLen) return;

        uint vehicleId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        uint skillId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(4));

        
        
        var current = RidingService.CurrentRide(session, player);

        if (current is null)
        {
            
            await RidingService.MountAsync(session, player, vehicleId, skillId, services);
            Logger.LogInformation("{Name} mounts riding vehicle {Vehicle} (skill {Skill})", player.Name, vehicleId, skillId);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsRideRidingPet,
                writer => writer.WriteBytes(BuildAnsRideRidingPet2719(ResultMounted, vehicleId, skillId)));
            return;
        }

        
        
        bool sameVehicle = current.PetActorHandle == vehicleId;
        await RidingService.DropAsync(session, player, services);
        Logger.LogInformation("{Name} gets off riding vehicle {Old} (skill {OldSkill}); request named {Vehicle} (skill {Skill})",
            player.Name, current.PetActorHandle, current.PetId, vehicleId, skillId);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsRideRidingPet,
            writer => writer.WriteBytes(BuildAnsRideRidingPet2719(
                sameVehicle ? ResultDismounted : ResultSwitchDropped, vehicleId, skillId)));
    }

 /// <summary>Builds the 18-byte 2719 AnsRideRidingPet body: <c>[u16 result][u32 vehicleId][u32 skillId][u32 0][u32 0]</c>.</summary>
    internal static byte[] BuildAnsRideRidingPet2719(ushort result, uint vehicleId, uint skillId)
    {
        using var pw = new PacketWriter(18);
        pw.WriteUInt16(result);    
        pw.WriteUInt32(vehicleId); 
        pw.WriteUInt32(skillId);   
        pw.WriteUInt32(0);         
        pw.WriteUInt32(0);         
        return pw.ToArray();
    }
}
