using RO2.Economy.Domain;
using RO2.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1597 ReqGameMallUpdate - Add item ids to one of the game-mall item lists</summary>
/// <remarks>
/// <para>
/// REQUEST, NONE envelope: <c>[u8 list][CompactLen n][n x u32 item id]</c> (read_side
/// Blob&lt;1&gt;,CompactLen,Vec&lt;U32&gt;). Requests name list 2 and carry 0 or 1 id
/// (an empty vector carries none). The ids are item ids, and the 1593 ReqGameMallItemList that the client sends right after
/// returns list 2 with that id prepended.
/// </para>
/// <para>
/// ANSWER 2869 AnsGameMallUpdate, read_side <c>Blob&lt;1&gt;,U16</c> = 3 bytes with NO length prefix:
/// <c>[u8 list echo][u16 result 0]</c>. Only requests that carry at least one id are answered, never the empty ones.
/// </para>
/// <para>
/// The ids of list 2 are stored per ACCOUNT behind IGameMallListRepository (new id in front, known ids skipped),
/// and ReqGameMallItemListHandler serves them back as the 2865 the client asks for next. The store is
/// written BEFORE the 2869 goes out because the client sends that 1593 right after the 2869.
/// </para>
/// </remarks>
public class GameMallUpdateHandler : ZoneRmiHandlerBase<GameMallUpdateHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGameMallUpdate;
    public override string Name => "GameMallUpdate";
    public GameMallUpdateHandler(IServiceScopeFactory s, ILogger<GameMallUpdateHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Upper bound on the id vector; requests carry at most one id.</summary>
    private const int MaxIds = 64;

    /// <summary>Smallest valid request: the list selector byte plus a CompactLen of at least 2 bytes.</summary>
    private const int MinRequestSize = 3;

    /// <summary>Size in bytes of one item id in the request vector (u32).</summary>
    private const int ItemIdSize = sizeof(uint);

    /// <summary>Result code of the 2869 answer; always 0.</summary>
    private const ushort ResultOk = 0;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        if (span.Length < MinRequestSize)
        {
            Logger.LogDebug("{Name}: 1597 of {Len} B, shorter than [u8 list][CompactLen]", player.Name, span.Length);
            return;
        }
        byte list = span[0];

        // CompactLen: [u8 width in {1,2,4,8}][width bytes LE]. Here it counts u32 ids rather than bytes,
        // so it is the bare CompactLen and not the byte-array envelope.
        if (!span[1..].TryReadCompactLength(out ulong n, out int compactSize))
        {
            Logger.LogDebug("{Name}: 1597 with a malformed CompactLen width {K}", player.Name, span[1]);
            return;
        }
        int pos = 1 + compactSize;
        if (n > MaxIds || pos + (int)n * ItemIdSize > span.Length)
        {
            Logger.LogDebug("{Name}: 1597 declares {N} ids in a {Len}-byte body", player.Name, n, span.Length);
            return;
        }

        var reader = new PacketReader(span[pos..]);
        var ids = new uint[(int)n];
        for (int i = 0; i < ids.Length; i++) ids[i] = reader.ReadUInt32();

        if (ids.Length == 0)
        {
            // The empty form is never answered.
            Logger.LogDebug("{Name} sent an empty game mall list {List} update, not answered", player.Name, list);
            return;
        }

        // List 2 is kept per account; the 2865 the client asks for next (1593, list 2) reads it back with the
        // new id in front. Only list 2 is stored: another list byte is acknowledged but not kept.
        if (list == GameMallListStore.AccountList && session.AccountId is uint accountId)
        {
            try
            {
                await services.GetRequiredService<IGameMallListRepository>().AddAsync(accountId, ids, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Game mall list 2 update failed for account {Account}", accountId);
            }
        }
        Logger.LogDebug("{Name} adds [{Ids}] to game mall list {List}", player.Name, string.Join(",", ids), list);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGameMallUpdate, writer =>
        {
            writer.WriteByte(list);   // echo of the list selector
            writer.WriteUInt16(ResultOk);   // result
        }, ct);
    }
}
