using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1635 ReqGuildTaxInquiry: the guild master asks the guild NPC for the collected territory tax.
/// </summary>
/// <remarks>
/// <para>
/// The request body is <c>[u32 NPC actor handle]</c>. Only the guild master is answered; any other caller gets no reply.
/// </para>
/// <para>
/// The reply, AnsGuildTaxInquiry (2935), is <c>[u16 result][u64 tax balance]</c>. This server keeps no territory tax, so the balance is always 0. Withdrawals are handled by <see cref="GuildTaxWithdrawHandler"/>.
/// </para>
/// </remarks>
public class GuildTaxInquiryHandler : ZoneRmiHandlerBase<GuildTaxInquiryHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildTaxInquiry;
    public override string Name => "GuildTaxInquiry";
    public GuildTaxInquiryHandler(IServiceScopeFactory s, ILogger<GuildTaxInquiryHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Expected length of the 1635 request body, in bytes.
 /// </summary>
    internal const int BodyLength = 4;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        if (span.Length < BodyLength)
        {
            Logger.LogDebug("{Name} ReqGuildTaxInquiry body is {Len} bytes, need {Need}", player.Name, span.Length, BodyLength);
            return;
        }
        var npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(span);

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (guild is null || !ReserveDeleteHandler.IsGuildMaster(guild, player.CharacterId))
        {
            Logger.LogDebug("{Name} asked NPC 0x{Npc:X8} for guild tax but is not a guild master; no answer",
                player.Name, npcHandle);
            return;
        }

        Logger.LogDebug("{Name} (master of guild {Guild}) asked NPC 0x{Npc:X8} for guild tax; no tax is kept, balance 0",
            player.Name, guild.Name, npcHandle);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildTaxInquiry,
            writer => writer.WriteBytes(BuildAnsGuildTaxInquiry2935(0, 0)), ct);
    }

 /// <summary>
 /// Builds the 2935 body: <c>[u16 result][u64 balance]</c>, 10 bytes, without envelope.
 /// </summary>
    internal static byte[] BuildAnsGuildTaxInquiry2935(ushort result, ulong balance)
    {
        var body = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(2), balance);
        return body;
    }
}
