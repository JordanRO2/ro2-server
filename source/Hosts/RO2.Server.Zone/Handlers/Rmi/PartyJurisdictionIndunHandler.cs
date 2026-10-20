using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1502 ReqPartyJurisdictionIndun: a party member's request about dungeon-instance ownership. Answered with
/// the neutral 2701 AnsPartyJurisdictionIndun (result 0, empty list).
/// </summary>
/// <remarks>
/// <para>
/// Both layouts come from the client's code alone. The request is one raw u16 (NONE family).
/// </para>
/// <para>
/// The answer is 20 raw bytes with no envelope, read as one fixed 20-byte block. Word 0 is the result; the client
/// then builds a vector of 8-byte elements from the pointer words at bytes 4 and 8 of the block.
/// So wire bytes 4..11 are raw pointers to the client and MUST be equal (zero here), or the client dereferences
/// server-supplied addresses; 12..19 (capacity and the trailing dword) are 0 too. The consumer
/// The client frees the vector, does nothing on result 0 and otherwise
/// shows the party result message. The old body was one byte, a short read of those 20.
/// </para>
/// <para>
/// What is missing: the server holds no party / instance "jurisdiction" state to apply the u16 to, so it is
/// only logged, and result 0 ("show nothing") is the reader's neutral answer. Sent to the requester only, and
/// only while they are in a party, as before.
/// </para>
/// </remarks>
public class PartyJurisdictionIndunHandler : ZoneRmiHandlerBase<PartyJurisdictionIndunHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyJurisdictionIndun;
    public override string Name => "PartyJurisdictionIndun";

 /// <summary>2701 body length, a fixed 20-byte block.</summary>
    internal const int AnsLength = 20;

    public PartyJurisdictionIndunHandler(IServiceScopeFactory s, ILogger<PartyJurisdictionIndunHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        if (payload.Length < 2)
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyJurisdictionIndun ({Len}B)", session.SessionId, payload.Length);
            return;
        }
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(payload.Span);

        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party == null) return;

        Logger.LogInformation("{Name}: 1502 PartyJurisdictionIndun {Value}; no instance-ownership state exists, answered with the neutral 2701",
            player.Name, value);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyJurisdictionIndun, BuildAnsJurisdictionIndun2701(0));
    }

 /// <summary>
 /// 2701 AnsPartyJurisdictionIndun, 20 raw bytes: <c>[u16 result][u16 0][u32 vecBegin 0][u32 vecEnd 0]
 /// [u32 vecCap 0][u32 0]</c>. The vector words stay zero: the stub copies through them as pointers.
 /// </summary>
    internal static byte[] BuildAnsJurisdictionIndun2701(ushort result)
    {
        var body = new byte[AnsLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }
}
