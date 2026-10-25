using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Network.Throttling;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1603 ReqGameMallFindChar - Resolve a character name to the account a mall gift goes to</summary>
/// <remarks>
/// <para>
/// REQUEST, NONE envelope: <c>[u8 0][WStr name]</c>, WStr = <c>[u8 widthTag][CompactLen n][n chars]</c>
/// (read with <see cref="ProudWStr"/>). The u8 is 0 and widthTag is 2, e.g. <c>00 02 01 0b</c> followed by
/// an 11-character name in UTF-16.
/// </para>
/// <para>
/// ANSWER 2876 AnsGameMallFindChar, 6 bytes: <c>[u16 result][u32 accountId]</c>. A hit has result 0 and the
/// u32 is the ACCOUNT id of the named character, not a character id: it is the u32 that account's own
/// 2799 gift list opens with. It is what the client then puts in the recipient field of
/// 1598 ReqGameMallPurchase. The match ignores case.
/// </para>
/// <para>
/// <see cref="ResultNotFound"/> is a code chosen by this server for a miss: 1 with account 0, in the same
/// 6-byte shape.
/// </para>
/// </remarks>
public class GameMallFindCharHandler : ZoneRmiHandlerBase<GameMallFindCharHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGameMallFindChar;
    public override string Name => "GameMallFindChar";
    public GameMallFindCharHandler(IServiceScopeFactory s, ILogger<GameMallFindCharHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Bytes before the name in the 1603 request: the leading u8 (always 0).</summary>
    private const int RequestNameOffset = 1;

    /// <summary>Longest character name read from a 1603 request, in UTF-16 chars.</summary>
    private const int MaxNameChars = 32;

    /// <summary>2876 result for a name that resolved to an account.</summary>
    private const ushort ResultFound = 0;
    /// <summary>Defensive code for an unknown name (see remarks).</summary>
    private const ushort ResultNotFound = 1;

    /// <summary>Lookups one session may make per <see cref="LookupWindowMs"/>. E-H12: the name scan is unindexed and
    /// answers name to account, so it is throttled per session. The figures are a
    /// defensive guess well above the one lookup per recipient entry a player makes.</summary>
    internal const int MaxLookupsPerSession = 10;
    internal const long LookupWindowMs = 10_000;

    private static readonly SlidingWindowRateLimiter<uint> s_lookups = new(MaxLookupsPerSession, LookupWindowMs);

    /// <summary>Spends one lookup of <paramref name="sessionId"/>; false when it is over its budget.</summary>
    internal static bool TryAdmitLookup(uint sessionId) => s_lookups.TryAcquire(sessionId);

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryAdmitLookup(player.SessionId))
        {
            Logger.LogInformation("{Name}: gift recipient lookups throttled", player.Name);
            await SendAnswerAsync(session, ResultNotFound, 0, ct);
            return;
        }

        int pos = RequestNameOffset; // skip the leading u8 (always 0)
        if (payload.Length < RequestNameOffset || !ProudWStr.TryRead(payload.Span, ref pos, out var name, MaxNameChars) || name.Length == 0)
        {
            Logger.LogDebug("{Name}: malformed 1603 body ({Len} B), no name to look up", player.Name, payload.Length);
            await SendAnswerAsync(session, ResultNotFound, 0, ct);
            return;
        }

        Character? target;
        try
        {
            target = await services.GetRequiredService<ICharacterRepository>().FindByNameIgnoreCaseAsync(name, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Name}: gift recipient lookup for '{Target}' failed", player.Name, name);
            target = null;
        }

        if (target == null)
        {
            Logger.LogDebug("{Name} looks up gift recipient '{Target}': not found", player.Name, name);
            await SendAnswerAsync(session, ResultNotFound, 0, ct);
            return;
        }

        Logger.LogDebug("{Name} looks up gift recipient '{Target}': character {Id}, account {Account}",
            player.Name, name, target.Id, target.AccountId);
        await SendAnswerAsync(session, ResultFound, target.AccountId, ct);
    }

    private static Task SendAnswerAsync(ProudSession session, ushort result, uint accountId, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsGameMallFindChar, w =>
        {
            w.WriteUInt16(result);
            w.WriteUInt32(accountId);
        }, ct);
}
