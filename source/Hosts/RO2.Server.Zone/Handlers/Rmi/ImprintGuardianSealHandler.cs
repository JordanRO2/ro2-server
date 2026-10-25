using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles ReqImprintGuardianSeal: imprints a guardian seal option on an item.</summary>
/// <remarks>
/// <para>
/// The request body is <c>[u32 NPC handle][u64 item uid A][u64 item uid B]</c>, 20 bytes.
/// </para>
/// <para>
/// This server does not roll imprint options. The reply is an all-zero 9-byte result, which is the reply for a no-imprint outcome, and nothing is consumed. The imprint value and trait come from a roll table this server does not have, and inventory items have no field that could keep an imprinted option.
/// </para>
/// </remarks>
public class ImprintGuardianSealHandler : ZoneRmiHandlerBase<ImprintGuardianSealHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqImprintGuardianSeal;
    public override string Name => "ImprintGuardianSeal";
    public ImprintGuardianSealHandler(IServiceScopeFactory s, ILogger<ImprintGuardianSealHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Fixed 9-byte body of the 3031 reply.
 /// </summary>
    private const int AnswerLength = 9;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 20)
        {
            Logger.LogWarning("{Name}: short 1694 body ({Len} B, retail is 20)", player.Name, payload.Length);
            return;
        }
        var reader = new PacketReader(payload.Span);
        var npcHandle = reader.ReadUInt32();  
        var itemUidA = reader.ReadUInt64();   
        var itemUidB = reader.ReadUInt64();   

        Logger.LogDebug("{Name} imprint guardian seal at NPC {Npc:X8}: item {A} / {B}; no seal roll table, nothing imprinted",
            player.Name, npcHandle, itemUidA, itemUidB);

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsImprintGuardianSeal, writer =>
        {
            writer.WriteZeros(AnswerLength);
        });
    }
}
