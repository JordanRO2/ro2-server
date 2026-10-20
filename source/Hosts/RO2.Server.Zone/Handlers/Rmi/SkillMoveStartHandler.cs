using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1522 ReqSkillMoveStart (Fixed 23B) - Start movement skill (e.g. charge, dash)</summary>
public class SkillMoveStartHandler : ZoneRmiHandlerBase<SkillMoveStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillMoveStart;
    public override string Name => "SkillMoveStart";

    public SkillMoveStartHandler(IServiceScopeFactory s, ILogger<SkillMoveStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Fixed 23B: [uint32 skillId][byte flags][...]. The start/end positions are not read.
        if (payload.Length < 23) return;
        var reader = new PacketReader(payload.Span);
        var skillId = reader.ReadUInt32();
        var flags = reader.ReadByte();

        Logger.LogDebug("{Name} skill move start {SkillId} flags={Flags}", player.Name, skillId, flags);

        // 2744 AnsSkillMoveStart: unicast ack to the caster. NONE-family RAW body: the client reads a
        // fixed Blob<6> (ReadBytesAligned(6)), so a length-exact 6-byte body cannot crash it. result 0 =
        // accepted; the skill id is echoed (see SkillPackets.BuildSkillMoveStartAck2744).
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsSkillMoveStart,
            SkillPackets.BuildSkillMoveStartAck2744(skillId));

        // The 2769 NfySkillMoveStart broadcast stays suppressed: it is a compact-BA (VarBlobC) message whose
        // layout is not defined here, and a wrong body crashes nearby clients. Do not send it raw.
    }
}
