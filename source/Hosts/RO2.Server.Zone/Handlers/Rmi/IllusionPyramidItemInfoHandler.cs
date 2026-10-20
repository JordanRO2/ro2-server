using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles ReqIllusionPyramidItemInfo: the pyramid window requests its reward catalog.</summary>
/// <remarks>
/// <para>
/// The request body is empty. The reply, AnsIllusionPyramidItemInfo (3011), is a constant 424-byte catalog: <c>[u16 0][CompactLen 42]</c> followed by 42 records of <c>[u32 item][u8 floor][u8 slot][u8 count][u8 Mileage_ID][u8 end][u8 broadcast]</c>.
/// </para>
/// <para>
/// The bytes come from <see cref="IllusionPyramidCatalog.Body3011Hex"/>, the single copy of the catalog. Draws and results use the same records, so the reward list and the results always agree.
/// </para>
/// </remarks>
public class IllusionPyramidItemInfoHandler : ZoneRmiHandlerBase<IllusionPyramidItemInfoHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqIllusionPyramidItemInfo;
    public override string Name => "IllusionPyramidItemInfo";

    private static readonly byte[] s_catalog3011 = Convert.FromHexString(IllusionPyramidCatalog.Body3011Hex);

    public IllusionPyramidItemInfoHandler(IServiceScopeFactory s, ILogger<IllusionPyramidItemInfoHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} queries illusion pyramid item info", player.Name);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidItemInfo, w => w.WriteBytes(s_catalog3011), ct);
    }
}
