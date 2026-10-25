using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Rules and packets of the union requests: 1582 ReqJoinUnion, answered by <see cref="AnswerJoinAsync"/>,
/// and 1583 ReqWithdrawUnion. Memberships are stored through <see cref="IUnionMembershipRepository"/>.
/// </summary>
/// <remarks>
/// A result of 0 means success; other codes map to client messages.
/// Joining requires level 50 or higher. A member may leave 14 days after joining.
/// </remarks>
public static class UnionRules
{
 /// <summary>2849/2850 success: the client joins, or leaves, the union.</summary>
    internal const ushort ResultOk = 0;

 /// <summary>2849 result: unavailable union id.</summary>
    internal const ushort ResultUnknownUnion = 24100;

 /// <summary>2850 result: the member has not yet passed the 14-day withdraw lock.</summary>
    internal const ushort ResultWithdrawTooSoon = 24101;

 /// <summary>2849 result: already joined a union.</summary>
    internal const ushort ResultAlreadyJoined = 24104;

 /// <summary>2850 result: not registered to a union.</summary>
    internal const ushort ResultNotInUnion = 24105;

 /// <summary>2849 result: level below the minimum (50).</summary>
    internal const ushort ResultLevelTooLow = 24106;

 /// <summary>How long after joining a character may leave: 14 days.</summary>
    internal static readonly TimeSpan WithdrawLock = TimeSpan.FromDays(14);

 /// <summary>The lowest level that may join: 50.</summary>
    internal const int JoinMinLevel = 50;

 /// <summary>The join time of a refused 2849: FF x8, meaning no join time.</summary>
    internal const ulong NoJoinTime = ulong.MaxValue;

 /// <summary>
 /// The 2849 answer to a join request, checked in order: an unknown union; the same union already stored;
 /// a different union already joined; a level below the minimum; otherwise success with a new membership.
 /// </summary>
    internal static ushort JoinResult(UnionMembership? current, uint requestedUnionId, bool unionExists, int level)
    {
        if (!unionExists) return ResultUnknownUnion;
        if (current is { } m) return m.UnionId == requestedUnionId ? ResultOk : ResultAlreadyJoined;
        if (level < JoinMinLevel) return ResultLevelTooLow;
        return ResultOk;
    }

 /// <summary>
 /// The 2850 answer to a withdraw request, checked in order: union id 0 or no membership; a different union;
 /// too soon after joining; otherwise success.
 /// </summary>
 /// <remarks>
 /// A nonzero id with no stored membership is answered with 0, so the client drops it.
 /// The union-quest check is left to the client.
 /// </remarks>
    internal static ushort WithdrawResult(UnionMembership? membership, uint requestedUnionId, long nowUnix)
    {
        if (requestedUnionId == 0) return ResultNotInUnion;
        if (membership is not { } m) return ResultOk;
        if (m.UnionId != requestedUnionId) return ResultNotInUnion;
        if (nowUnix - m.JoinedAtUnix < (long)WithdrawLock.TotalSeconds) return ResultWithdrawTooSoon;
        return ResultOk;
    }

 /// <summary>
 /// Body of 2849 AnsJoinUnion: [u32 union id][u64 join time][u16 result], 14 bytes.
 /// </summary>
    internal static void WriteAnsJoinUnion(PacketWriter writer, uint unionId, ulong joinedAtUnix, ushort result)
    {
        writer.WriteUInt32(unionId);
        writer.WriteUInt64(joinedAtUnix);
        writer.WriteUInt16(result);
    }

 /// <summary>
 /// Answers a 1582 join request with a 2849, and stores the membership when the join is new.
 /// A refusal carries the requested id and <see cref="NoJoinTime"/>.
 /// </summary>
    public static async Task AnswerJoinAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player,
        IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        if (payload.Length < 4)
        {
            logger.LogWarning("{Name}: ReqJoinUnion of {Len} bytes, expected 4 ([u32 unionId]); not answered",
                player.Name, payload.Length);
            return;
        }
        uint unionId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        bool unionExists = services.GetRequiredService<GameDataStore>().UnionInfo.Any(u => u.Union_ID == unionId);

        var memberships = services.GetRequiredService<IUnionMembershipRepository>();
        var current = await memberships.GetAsync(player.CharacterId, ct);
        ushort result = JoinResult(current, unionId, unionExists, player.Level);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (result == ResultOk && current is null)
        {
            if (await memberships.JoinAsync(player.CharacterId, unionId, now, ct))
                current = new UnionMembership(unionId, now);
            else
            {
                
                current = await memberships.GetAsync(player.CharacterId, ct);
                result = current is { } stored && stored.UnionId == unionId ? ResultOk : ResultAlreadyJoined;
            }
        }

        logger.LogInformation("{Name} joins union {Union}: result {Result} (stored {Stored})",
            player.Name, unionId, result, current is { } c ? $"{c.UnionId} since {c.JoinedAtUnix}" : "none");

        await session.SendRmiAsync(ZoneS2COpcodes.AnsJoinUnion, writer =>
        {
            if (result == ResultOk && current is { } joined)
                WriteAnsJoinUnion(writer, joined.UnionId, (ulong)joined.JoinedAtUnix, ResultOk);
            else
                WriteAnsJoinUnion(writer, unionId, NoJoinTime, result);
        });
    }
}
