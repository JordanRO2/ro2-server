using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;
using RO2.Characters.Application;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1564 ReqCanRenameChaName - the rename window's "check name" step:
/// <c>[u64 scrollUid][WStr currentName][WStr newName]</c> (see <see cref="CharacterRename"/>).
/// Answers AnsCanRenameChaName (2814), a fixed 4-byte <c>[u32 result]</c>.</summary>
public class CanRenameChaNameHandler : ZoneRmiHandlerBase<CanRenameChaNameHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCanRenameChaName;
    public override string Name => "CanRenameChaName";

    public CanRenameChaNameHandler(IServiceScopeFactory s, ILogger<CanRenameChaNameHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        uint result;
        if (!CharacterRename.TryParse(payload.Span, out var request))
        {
            
            result = CharacterRename.ResultRefused;
            Logger.LogWarning("{Name} sent an unparseable ReqCanRenameChaName ({Len} bytes)", player.Name, payload.Length);
        }
        else
        {
            var check = await RenameCheckFlow.CheckAsync(player.Name, player.CharacterId, request.ScrollUid, request.NewName,
                services.GetRequiredService<INameFilter>(), services.GetRequiredService<ICharacterGameData>(),
                services.GetRequiredService<IInventoryRepository>(), services.GetRequiredService<ICharacterRepository>());
            result = CharacterRename.Code(check.Refusal);
            Logger.LogInformation("{Name} asks to rename to '{New}' (scroll {Uid}): {Result}{Reason}",
                player.Name, request.NewName, request.ScrollUid, result, check.Reason is null ? "" : $" ({check.Reason})");
        }

        
        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsCanRenameChaName, writer =>
        {
            writer.WriteUInt32(result);
        }, ct);
    }
}
