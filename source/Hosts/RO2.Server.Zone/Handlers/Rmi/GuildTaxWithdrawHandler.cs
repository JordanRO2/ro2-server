using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1636 ReqGuildTaxWithdraw: a guild master asks to withdraw the guild's territory tax.
/// </summary>
/// <remarks>
/// <para>
/// The request body is <c>[u32 NPC actor handle][u64 amount]</c>, 12 bytes without envelope.
/// </para>
/// <para>
/// This server keeps no territory tax, so the balance is always 0. The request is logged and nothing is paid. No reply is sent.
/// </para>
/// <para>
/// Paying out a real tax balance would need territory and siege ownership, tax accrual, a persistent per-guild balance, and a withdrawal into the guild funds that respects the vault cap.
/// </para>
/// </remarks>
public class GuildTaxWithdrawHandler : ZoneRmiHandlerBase<GuildTaxWithdrawHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildTaxWithdraw;
    public override string Name => "GuildTaxWithdraw";
    public GuildTaxWithdrawHandler(IServiceScopeFactory s, ILogger<GuildTaxWithdrawHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Body length in bytes: a u32 NPC handle followed by a u64 amount.
 /// </summary>
    internal const int BodyLength = 12;

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        if (span.Length < BodyLength)
        {
            Logger.LogDebug("{Name} ReqGuildTaxWithdraw body is {Len} bytes, need {Need}",
                player.Name, span.Length, BodyLength);
            return Task.CompletedTask;
        }

        var npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var amount = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(4));
        Logger.LogInformation(
            "{Name} asked NPC 0x{Npc:X8} to withdraw {Amount} guild tax; this server keeps no territory tax (balance 0), nothing paid, no reply (every retail 2936 reports a positive balance)",
            player.Name, npcHandle, amount);
        return Task.CompletedTask;
    }
}
