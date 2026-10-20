using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using Base1592 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallItemListAllHandler>;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1592 ReqGameMallItemListAll. Requests the cash mall catalog, answered with 2866 AnsGameMallItemListAll.</summary>
/// <remarks>
/// The catalog is served from the shipped <see cref="CashMallCatalog"/>. Purchases are handled by
/// GameMallPurchaseHandler, which prices products from the same catalog. If the catalog file is missing, an
/// empty list is sent (<see cref="BuildEmptyCatalog2866"/>).
/// </remarks>
public class ReqGameMallItemListAllHandler : Base1592
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGameMallItemListAll;
    public override string Name => "ReqGameMallItemListAll";
    public ReqGameMallItemListAllHandler(IServiceScopeFactory s, ILogger<ReqGameMallItemListAllHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        var catalog = CashMallCatalog.Shipped;
        if (catalog is null)
            Logger.LogWarning("Session {Id}: {Path} is missing or unreadable; serving an empty cash mall",
                session.SessionId, CashMallCatalog.ShippedPath);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGameMallItemListAll, catalog?.Build2866() ?? BuildEmptyCatalog2866(), ct);
    }

    /// <summary>
    /// The 2866 body with an empty catalog: <c>[u16 result=0][u32 blobLen=2][u16 count=0]</c>, for a server whose
    /// catalog file is missing.
    /// </summary>
    /// <remarks>
    /// The body is <c>U16, VarBlob</c>: the u32 is a length prefix that must describe exactly the bytes after it,
    /// and the item count word is inside the blob. A non-zero result makes the client show a cash-shop error
    /// message; count 0 leaves the mall empty.
    /// </remarks>
    public static byte[] BuildEmptyCatalog2866() => new byte[]
    {
        0x00, 0x00,             // u16 result = 0 (success)
        0x02, 0x00, 0x00, 0x00, // u32 VarBlob length = the 2-byte count that follows
        0x00, 0x00,             // u16 item count = 0
    };
}
