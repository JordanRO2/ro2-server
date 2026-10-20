using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1551 ReqGetAccountGift - Claim an account gift</summary>
/// <remarks>
/// <para>
/// Request, U32 family, 35 bytes:
/// <c>[u32 len=31][u32 @4][u8 @8][u64 character GID @9][u16 @17 = 0][16-byte gift GUID @19]</c> —
/// The GUID is the only field that changes between claims; the answer echoes it.
/// </para>
/// <para>
/// The answer, 2800 AnsGetAccountGift, is a fixed 185-byte grant: it
/// echoes body+4, the GID and the GUID, then carries the granted item id and the full 126-byte record
/// of the item it just put in the bag. The box lives in AccountGiftStore (per account, Mongo), and the
/// claim itself is AccountGiftClaims.HandleAsync.
/// </para>
/// <para>
/// The grant's layout: NONE
/// envelope, 185 bytes, <c>[u32 accountId @0][u32 result @4: 0 = granted, 3 = refused][u64 character GID @8][16-byte
/// GUID @16][u32 itemId @32][u32 count @36][15 zero bytes @40][u8 @55 = the 2799 record's byte @39][3 zero bytes @56]
/// [126-byte bag item record @59]</c>. Item id and count equal the 2799 record's u32 @16 and u16 @20; the bag
/// record's item id (@67) is the gift's on a grant, and the record is all zero on a result-3 refusal. The answer
/// follows its 1551 closely and always echoes the GUID.
/// </para>
/// <para>
/// Silence is also the response to a gift that is not in the box: a claim whose
/// GUID appears in no 2799 page the session was sent gets no 2800, and neither does a 1551 that names a GUID the
/// same session had already been granted. Every 2800 echoes the GUID it answers. The only refusal form,
/// <c>u32 @4 = 3</c> with the record zeroed (for listed gifts), still carries the refused gift's
/// real item id and count, so it answers "this gift exists but cannot be taken", not "no such gift".
/// </para>
/// </remarks>
public class GetAccountGiftHandler : ZoneRmiHandlerBase<GetAccountGiftHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGetAccountGift;
    public override string Name => "GetAccountGift";

    public GetAccountGiftHandler(IServiceScopeFactory s, ILogger<GetAccountGiftHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    // The box is real: AccountGiftStore files the play-time rewards and the daily attendance gift, 1550 lists them
    // and 2798 announces them. AccountGiftClaims moves the claimed gift into the bag and answers 2800 (see its
    // remarks); a GUID the box does not hold stays unanswered.
    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
        => AccountGiftClaims.HandleAsync(session, player, payload, services, Logger, ct);
}
