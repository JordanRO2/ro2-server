using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1641 ReqSiegeArmsUseSkill, a fixed 24-byte request. Uses a siege weapon skill.</summary>
public class SiegeArmsUseSkillHandler : ZoneRmiHandlerBase<SiegeArmsUseSkillHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSiegeArmsUseSkill;
    public override string Name => "SiegeArmsUseSkill";

    public SiegeArmsUseSkillHandler(IServiceScopeFactory s, ILogger<SiegeArmsUseSkillHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        if (payload.Length < 24) return;
        var reader = new PacketReader(payload.Span);
        var skillId = reader.ReadUInt32();
        reader.ReadUInt32(); 
        reader.ReadUInt32(); 
        var targetX = reader.ReadSingle();
        var targetY = reader.ReadSingle();
        var targetZ = reader.ReadSingle();

        Logger.LogDebug("{Name} fires siege skill {SkillId} at ({X}, {Y}, {Z})", player.Name, skillId, targetX, targetY, targetZ);

        
        
        
        
        
        
        var store = services.GetRequiredService<GameDataStore>();
        if (!store.SkillLevels.TryGetValue(skillId, out var levelData))
        {
            
            
            Logger.LogDebug("{Name} sent 1641 with unknown skill {SkillId}; no 2943", player.Name, skillId);
            return;
        }
        var cooldown = levelData.Cooldown_Time;

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var arm = SiegeService.GetOperatedArm(session.SessionId);
        if (arm is not null)
        {
            uint group = SiegeService.CooldownGroupOf(skillId, store.Skills);
            uint totalMs = (uint)Math.Round(Math.Max(0f, cooldown) * 1000.0);
            if (!SiegeService.TryStartArmCooldown(arm.Handle, group, skillId, totalMs, DateTime.UtcNow, out var running))
            {
                Logger.LogDebug("{Name} fired {SkillId} from arm {Arm} while group {Group} cools down (opened by {Opener}, ends {End:O})",
                    player.Name, skillId, arm.Handle, group, running.SkillId, running.EndsAtUtc);
                await session.SendRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseSkill,
                    writer => writer.WriteBytes(SiegePackets.BuildSiegeArmsUseSkillRefusal2943(SiegePackets.ResultArmSkillCoolingDown)));
                return;
            }
        }

        await session.SendRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseSkill,
            writer => writer.WriteBytes(EconomyMiscPackets.BuildSiegeArmsUseSkillAck(skillId, cooldown)));

        
        
        
        
        
        
        
        
        
        
        
        
    }
}
