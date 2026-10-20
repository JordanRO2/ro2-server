using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1545 ReqPartyDiceChattingCmd - the party-chat dice command: roll a number in [min, max] and tell the
/// whole party.
/// </summary>
/// <remarks>
/// <para>
/// <b>Request</b>, 4 bytes, NONE envelope: <c>[u16 min][u16 max]</c> (<c>01006400</c> = 1..100,
/// <c>01004300</c> = 1..67). The client builds it from the party-chat dice command: one number N sends
/// min 1 / max N, two numbers go as typed, and a number outside 1..65535 is refused locally with string 10229
/// "Input range is too large." before anything is sent. The client checks EACH number on its own and never
/// compares min with max, so a 0 cannot reach us but <c>/dice 100 1</c> does, as min 100 / max 1.
/// </para>
/// <para>
/// <b>Answer</b>: 2788 NfyPartyDiceChattingCmd to every party member, the roller included, about 0.2 s after the
/// 1545. It echoes the request's min and max with a roll inside them and the roller's party GID, which the
/// consumer looks up in the roster
/// (see <see cref="PartyStatePackets.BuildNfyPartyDiceChattingCmd"/>). There is no other ack.
/// </para>
/// <para>
/// <b>Not answered</b>: a player with no party (the consumer prints nothing without a roster member), a body
/// that is not 4 bytes, a 0 (which the client cannot send), and min &gt; max, which the
/// client CAN send. That last silence is a PLACEHOLDER: the expected answer to a min &gt; max or a very wide
/// range is not defined. The 2788 consumer does have a server-side refusal, result 1 or 2
/// -> string 10229, but what triggers it is unknown, and swapping the bounds, rolling anyway or answering
/// result 1/2 would each be an invented behaviour.
/// </para>
/// </remarks>
public class PartyDiceChattingCmdHandler : ZoneRmiHandlerBase<PartyDiceChattingCmdHandler>
{
    /// <summary>1545 body length: [u16 min][u16 max].</summary>
    private const int RequestLength = 4;

    /// <summary>1545 body offset of the u16 max (min is at 0).</summary>
    private const int MaxOffset = 2;

    /// <summary>Lowest bound the client can send: it refuses a 0 locally, so min is at least 1.</summary>
    private const ushort MinAllowedBound = 1;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyDiceChattingCmd;
    public override string Name => "PartyDiceChattingCmd";

    public PartyDiceChattingCmdHandler(IServiceScopeFactory s, ILogger<PartyDiceChattingCmdHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != RequestLength)
        {
            Logger.LogWarning("{Name}: 1545 body is {Len} bytes, expected {Expected}; ignored",
                player.Name, payload.Length, RequestLength);
            return;
        }
        ushort min = BinaryPrimitives.ReadUInt16LittleEndian(payload.Span);
        ushort max = BinaryPrimitives.ReadUInt16LittleEndian(payload.Span.Slice(MaxOffset));

        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party == null)
        {
            Logger.LogDebug("{Name}: 1545 dice {Min}-{Max} outside a party; nothing sent", player.Name, min, max);
            return;
        }

        // A 0 cannot come from the client. min > max can (the client never compares the two); the expected
        // answer to it is not defined, so this silence is a placeholder (see the remarks).
        if (min < MinAllowedBound || min > max)
        {
            Logger.LogInformation("{Name}: 1545 dice range {Min}-{Max} has no defined answer; nothing sent",
                player.Name, min, max);
            return;
        }

        var roll = (ushort)Random.Shared.Next(min, max + 1);
        Logger.LogDebug("{Name} rolled {Roll} ({Min}-{Max})", player.Name, roll, min, max);

        await partyService.BroadcastBodyToPartyAsync(party, ZoneS2COpcodes.NfyPartyDiceChattingCmd,
            PartyStatePackets.BuildNfyPartyDiceChattingCmd(PartyAnswerPackets.MemberGid(player), min, max, roll));
    }
}
