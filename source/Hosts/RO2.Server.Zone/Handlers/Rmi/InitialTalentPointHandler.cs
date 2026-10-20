using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles ReqPCInitialTalentPoint: answers with the current talent pool. Nothing is reset.</summary>
/// <remarks>
/// <para>
/// The request is a compact envelope around a 12-byte blob holding the own PC handle and a u32 value at blob offset 8. The value is read for logging only.
/// </para>
/// <para>
/// The shipped client has no path that sends this request. The reply carries <see cref="StatService.TalentPointsRemaining"/> over the stored talent tree, the same pool that the skill reset and the login talent packets report. No talent is reset here: a free reset would only be reachable from a modified client. The real reset paths are the Item_Type 26 scroll and the Talent_Reset job change.
/// </para>
/// </remarks>
public class InitialTalentPointHandler : ZoneRmiHandlerBase<InitialTalentPointHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCInitialTalentPoint;
    public override string Name => "InitialTalentPoint";

 /// <summary>
 /// Length of the 12-byte request blob.
 /// </summary>
    private const int BlobLength = 12;

    public InitialTalentPointHandler(IServiceScopeFactory s, ILogger<InitialTalentPointHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        uint? a1 = payload.Span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) && blob.Length >= BlobLength
            ? BinaryPrimitives.ReadUInt32LittleEndian(blob[8..])
            : null;

        var store = services.GetRequiredService<GameDataStore>();
        byte pool = StatService.TalentPointsRemaining(store, player.Job, player.Level,
            player.LearnedSkills.Select(kv => (kv.Key, kv.Value)));

        Logger.LogWarning(
            "{Name} sent 1311 ReqPCInitialTalentPoint (a1={A1}), which the shipped client has no path to send; nothing is reset, answering the current pool {Pool}",
            player.Name, a1?.ToString() ?? "unparsed", pool);

        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnInitTalentPoint,
            writer => writer.WriteBytes(QuestProgressPackets.BuildInitTalentPoint2544(player.EntityId, pool)));
    }
}
