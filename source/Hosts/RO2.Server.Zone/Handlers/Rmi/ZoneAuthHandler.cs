using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.Enums;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

using NetworkZoneRmiBase = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ZoneAuthHandler>;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handler for C2S RMI 1303 (0x0517) - Zone Authentication
/// Client sends this in response to S2C RMI 2303 (ZoneGreeting) with:
///   - Session GUID (16 bytes)
///   - Client IP as wide string
///   - Port and other connection data
///
/// After receiving, server responds with:
///   - S2C RMI 2304 (ZoneAuthAck)
///   - S2C RMI 2307 (ZoneCharacterList)
///
/// This handler uses the Network-level base class (not the Zone-level one)
/// because it runs BEFORE player state is established.
///
/// Example plaintext (67 bytes total, 62 bytes payload):
///   01 3C 3C 00 F2 D2 00 00 00 00 C7 4F 71 D7 98 35
///   86 44 AA BB 17 35 FC E1 DE 4E 8B 33 00 00 31 00
///   37 00 32 00 2E 00 32 00 32 00 2E 00 30 00 2E 00
///   31 00 00 00 18 00 46 91 03 01 03 00 00 00
/// </summary>
public class ZoneAuthHandler : NetworkZoneRmiBase
{
    /// <summary>Bytes of the 1303 request ahead of the session GUID: compact length (marker + value, 2), u16 selfLen,
    /// u16 msgTag (54002) and the zero u32 at blob+4.</summary>
    private const int EnvelopeHeaderLen = 10;

    /// <summary>RMI 2304 ZoneAuthAck message tag (0xD2F3), blob+2.</summary>
    private const ushort MsgTagZoneAuthAck = 54003;

    public override ushort Opcode => ZoneC2SOpcodes.ReqLoginChannel;
    public override string Name => "ZoneAuth";

    public ZoneAuthHandler(IServiceScopeFactory scopeFactory, ILogger<ZoneAuthHandler> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogInformation("Session {SessionId}: ZoneAuth (RMI 1303) received ({Size} bytes): {Hex}",
            session.SessionId, payload.Length, Convert.ToHexString(payload.Span));

        // S-H7: one authentication per live connection. A 1303 on a connection that already holds an account,
        // or whose character is in the world, would swap the account under it (and start a second hold loop);
        // it is refused BEFORE the token is consumed, so the token it carries is not burned.
        if (IsReauthentication(session, services.GetRequiredService<PlayerStateManager>()))
        {
            Logger.LogWarning("Session {SessionId}: ZoneAuth refused - the connection is already authenticated "
                + "(account {AccountId})", session.SessionId, session.AccountId);
            return;
        }

        // Parse client auth data
        // Layout (compact-length envelope, then the fields):
        //   [01]                - compact-length width marker (1 byte)
        //   [3C]                - compact length (60)
        //   [3C 00]             - u16 selfLen (60)
        //   [F2 D2]             - u16 msgTag 54002 (RMI 1303)
        //   [00 00 00 00]       - u32 blob+4, zero
        //   [GUID:16]           - session GUID
        //   [u32 accountId]     - the account the client claims (not trusted)
        //   [WideString]        - client IP (null-terminated UTF-16LE)
        //   [remaining]         - port + other data

        if (payload.Length < EnvelopeHeaderLen)
        {
            Logger.LogWarning("Session {SessionId}: ZoneAuth payload too short ({Size} bytes)",
                session.SessionId, payload.Length);
            return;
        }

        var reader = new PacketReader(payload.Span);
        reader.Skip(EnvelopeHeaderLen);          // compact length (2) + selfLen (2) + msgTag (2) + blob+4 (4)

        // Read session GUID (16 bytes) if available
        Guid sessionGuid = Guid.Empty;
        if (reader.Remaining >= 16)
        {
            var guidBytes = reader.ReadBytes(16).ToArray();
            sessionGuid = new Guid(guidBytes);
            Logger.LogDebug("Session {SessionId}: Client session GUID: {Guid}",
                session.SessionId, sessionGuid);
        }

        // Read the account ID the client CLAIMS (uint32, between GUID and IP) — but do NOT trust it; it is
        // validated against the Login-issued token below, AFTER every PacketReader read is done (the reader
        // is a ref struct and cannot survive an await).
        uint claimedAccountId = 0;
        if (reader.Remaining >= 4)
            claimedAccountId = reader.ReadUInt32();

        // Read client IP as wide string (null-terminated UTF-16LE)
        string clientIp = "";
        if (reader.Remaining >= 4)
        {
            clientIp = reader.ReadWideString();
            Logger.LogDebug("Session {SessionId}: Client IP: {IP}", session.SessionId, clientIp);
        }

        // Read remaining data (port, flags)
        if (reader.Remaining >= 2)
        {
            var clientPort = reader.ReadUInt16();
            Logger.LogDebug("Session {SessionId}: Client port: {Port}", session.SessionId, clientPort);
        }

        // All PacketReader reads are done — now it is safe to await. Trust the Login handoff, not the packet:
        // look the session GUID up in the shared token store and use the AccountId the Login server bound to
        // it (one-time consume). No valid token means the client skipped Login or spoofed an id — reject
        // rather than load whatever account it named.
        var validated = await services.GetRequiredService<RO2.Application.Interfaces.IZoneAuthTokenRepository>()
            .ConsumeAsync(sessionGuid);
        if (validated is null)
        {
            Logger.LogWarning("Session {SessionId}: ZoneAuth REJECTED — no valid Login token for GUID {Guid} "
                + "(claimed account {Claimed})", session.SessionId, sessionGuid, claimedAccountId);
            return;
        }
        var accountId = validated.Value;

        // This connection now holds the account: record it where the Login server looks before accepting
        // another 12002 for it, and keep the record alive until the account leaves this Zone. See
        // OnlineAccountRegistry for the behaviour (12502 = 1008 to the new login, 2305 reason 15310
        // to this one). This 1303 is the only claim: a channel move comes back on a new connection that
        // authenticates with 1421 and sends no 1303, so HoldOnlineAccountAsync
        // follows the account onto that connection instead of releasing it.
        // S-H7: the claim runs before the connection takes the account or is acknowledged. When it fails the
        // connection stays unauthenticated and the token is given back (IssueAsync), so the client's retry
        // is not locked out by a token this attempt burned.
        var registry = services.GetRequiredService<IOnlineAccountRegistry>();
        var owner = OnlineAccountRules.OwnerFor(session.SessionId);
        try
        {
            await registry.ClaimAsync(accountId, owner, ct);
        }
        catch (Exception ex)
        {
            // session.AccountId is only set after the claim succeeds, so the connection stays unauthenticated.
            Logger.LogError(ex, "Session {SessionId}: could not record account {AccountId} as online; ZoneAuth refused",
                session.SessionId, accountId);
            try
            {
                await services.GetRequiredService<RO2.Application.Interfaces.IZoneAuthTokenRepository>()
                    .IssueAsync(sessionGuid, accountId, TokenReissueTtl);
            }
            catch (Exception reissue)
            {
                Logger.LogWarning(reissue, "Session {SessionId}: could not give the Login token back after the failed claim",
                    session.SessionId);
            }
            return;
        }
        session.AccountId = accountId;

        // A 1303 is a fresh login of the account, so no channel move of it is still in flight: revoke any
        // ticket a 1420 left pending, or a 1421 replaying that ticket would authenticate a second connection
        // of the account (ChannelMoveTicketStore).
        services.GetRequiredService<ChannelMoveTicketStore>().RevokeAccount(accountId);

        Logger.LogInformation("Session {SessionId}: ZoneAuth accepted - AccountId={AccountId} (claimed {Claimed}), "
            + "GUID={Guid}, IP={IP}",
            session.SessionId, accountId, claimedAccountId, sessionGuid, clientIp);

        // Send S2C RMI 2304 (ZoneAuthAck)
        // Layout: [compactLen 01 0A][u16 selfLen = 10][u16 msgTag = 0xD2F3][u32 0 (blob+4)][u16 0 (blob+8)]
        //   Full plaintext: 01 00 01 00 09 01 0A 0A 00 F3 D2 00 00 00 00 00 00
        //   After 5-byte header: 01 0A 0A 00 F3 D2 00 00 00 00 00 00 (12 bytes payload)
        await session.SendRmiAsync(ZoneS2COpcodes.ZoneAuthAck, writer =>
        {
            writer.WriteCompactBABody(p =>
            {
                p.WriteUInt16(MsgTagZoneAuthAck); // blob+2  54003
                p.WriteUInt32(0);                 // blob+4  never read
                p.WriteUInt16(0);                 // blob+8
            });
        });

        Logger.LogInformation("Session {SessionId}: ZoneAuthAck (RMI 2304) sent", session.SessionId);

        // Charlist (2307) is sent by ZoneClientInitHandler (1414), NOT here
        // Sending it here causes a double send which desyncs the encrypted counter

        // Singletons (Program.cs), so they outlive this message's scope and the hold loop may keep them.
        var players = services.GetRequiredService<PlayerStateManager>();
        var broadcast = services.GetRequiredService<BroadcastService>();
        var tickets = services.GetRequiredService<ChannelMoveTicketStore>();

        // One hold loop per connection: a loop this connection already has (after a 1302 logout cleared the
        // account and a new 1303 came in) is cancelled, so two loops never heartbeat the same owner. It is NOT
        // cancelled when the connection closes: that loop is what hands the entry to the connection a channel
        // move lands on, and it ends by itself (SuccessorWaitOver).
        var loopCts = new CancellationTokenSource();
        if (HoldLoops.TryGetValue(session, out var previousLoop))
        {
            HoldLoops.Remove(session);
            previousLoop.Cancel();
        }
        HoldLoops.Add(session, loopCts);
        _ = Task.Run(() => HoldOnlineAccountAsync(session, registry, accountId, owner, players, broadcast, tickets, Logger, loopCts.Token));
    }

    /// <summary>How long a Login token given back after a failed online-account claim stays valid: the
    /// lifetime the Login server issues it with (SelectServerHandler).</summary>
    private static readonly TimeSpan TokenReissueTtl = TimeSpan.FromMinutes(5);

    /// <summary>The cancellation of each connection's hold loop, tied to the session object so it goes away
    /// with the connection (S-H7).</summary>
    private static readonly ConditionalWeakTable<ProudSession, CancellationTokenSource> HoldLoops = new();

    /// <summary>Hold loops running now (diagnostics and tests).</summary>
    internal static int ActiveHoldLoops => Volatile.Read(ref _activeHoldLoops);
    private static int _activeHoldLoops;

    /// <summary>
    /// Whether a 1303 or 1421 on <paramref name="session"/> would re-authenticate it: the connection already
    /// holds an account, or a character of it is in the world (S-H7).
    /// </summary>
    internal static bool IsReauthentication(ProudSession session, PlayerStateManager players)
        => session.AccountId is not null || players.TryGetPlayer(session.SessionId, out _);

    /// <summary>
    /// 2305 OnCharacterDisconnectNotify for a connection dropped because the account logged in again
    /// elsewhere: the logout body (SessionExitPackets.DisconnectNotify2305) with reason 15310 (0x3BCE) at
    /// blob+8, e.g. <c>01 0a 0a 00 f5 d2 00 00 00 00 ce 3b</c>; the client compares blob+8 with 15310 and
    /// shows string 2031 instead of logging out quietly.
    /// </summary>
    internal static byte[] BuildDuplicateLoginNotify2305()
        => SessionExitPackets.BuildDisconnectNotify2305(DuplicateLoginReason);

    /// <summary>2305 reason for a duplicate login (blob+8; see <see cref="BuildDuplicateLoginNotify2305"/>).</summary>
    internal const ushort DuplicateLoginReason = 15310;

    /// <summary>
    /// How long a connection dropped for a duplicate login stays open after its 2305. A kicked
    /// connection sends nothing after that 2305, and
    /// the client's dialog callback returns it to the login screen, so this only closes connections whose
    /// client does not go.
    /// </summary>
    private static readonly TimeSpan DuplicateLoginGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a closed connection's entry waits for the account to come back once no channel move is in
    /// flight. It covers the gap between a 1421 claiming its ticket and the 1417 that adds the new
    /// connection's PlayerState, which is what <see cref="FindSuccessorSessionId"/> looks for: 1417 follows
    /// the 2557 answer to 1421 within a few tens of milliseconds
    /// (ChannelMoveTicketStore), and the rest is LoginPCHandler's database reads. The same window
    /// ZoneService uses before it locks a dropped party member.
    /// </summary>
    internal static readonly TimeSpan SuccessorSettle = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The connection that now carries <paramref name="accountId"/> after <paramref name="departedSessionId"/>
    /// closed: the newest (highest id) connected session with a PlayerState of that account, or null.
    /// A 1421 channel-move connection gets its PlayerState at the 1417 that follows it, with the account the
    /// ticket restored (LoginPCHandler copies session.AccountId into PlayerState.AccountId).
    /// </summary>
    internal static uint? FindSuccessorSessionId(IEnumerable<PlayerState> players, uint accountId,
        uint departedSessionId, Func<uint, bool> isConnected)
    {
        uint? best = null;
        foreach (var p in players)
        {
            if (p.AccountId != accountId || p.SessionId == departedSessionId || !isConnected(p.SessionId)) continue;
            if (best is null || p.SessionId > best) best = p.SessionId;
        }
        return best;
    }

    /// <summary>
    /// Whether a closed connection's entry stops waiting for the account to come back. It waits at most a
    /// ticket lifetime plus <see cref="SuccessorSettle"/> from the close (<paramref name="orphanedAt"/>),
    /// and only <see cref="SuccessorSettle"/> from <paramref name="settleFrom"/>, the first moment no move
    /// was pending or a new login asked for the account. The ticket count is a hint, not per account:
    /// tickets expire lazily, so a stale one can only make a real drop wait longer, never cut a move short.
    /// </summary>
    internal static bool SuccessorWaitOver(DateTime orphanedAt, DateTime? settleFrom, DateTime now)
        => now - orphanedAt >= ChannelMoveTicketStore.Lifetime + SuccessorSettle
           || (settleFrom is { } from && now - from >= SuccessorSettle);

    /// <summary>
    /// 1302 logout (LogoutHandler): the account has left the world, so its <see cref="IOnlineAccountRegistry"/>
    /// entry is removed now, not when the connection closes. The hold loop of that connection then finds
    /// no entry at its next heartbeat and ends (<see cref="HoldOnlineAccountAsync"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The account can log in again right after a logout. Before the 1302 released the entry, it lived as long
    /// as the connection plus the orphan wait (about 7-8.6 s after the 1302; the logged-out connection stays open
    /// about 5 s, LogoutHandler): every re-login inside that window was answered 1008 and the logged-out
    /// connection was sent a 2305 15310.
    /// </para>
    /// <para>
    /// "No PlayerState" cannot stand for "logged out" (it is also true at character select), so the 1302
    /// itself releases. See <see cref="LogoutReleasableOwners"/> for which holder is released.
    /// </para>
    /// <para>
    /// Releasing is only safe because the connection cannot put the account back in the world afterwards:
    /// LogoutHandler clears <see cref="ProudSession.AccountId"/> before calling this, so a 1417 on the
    /// logged-out connection is refused (LoginPCHandler requires it) instead of creating a PlayerState that
    /// no entry and no hold loop covers. A new 1303 re-authenticates it and claims the entry again. A
    /// channel move started before the 1302 cannot bring the account back either: this revokes every
    /// ticket the account still holds (ChannelMoveTicketStore.RevokeAccount) before it releases the
    /// entry, so a 1421 (on any connection) carrying the ticket of a 1420 sent BEFORE the 1302 finds
    /// nothing. The client closes that connection about 2 ms after the 2556 (ChannelMoveHandler), so
    /// only a modified client sends a 1302 in between.
    /// </para>
    /// </remarks>
    internal static async Task ReleaseOnlineAccountAtLogoutAsync(ProudSession session, uint accountId,
        IServiceProvider services, ILogger logger)
    {
        // In memory and first, so that it holds even when the registry below cannot be reached.
        var revoked = services.GetRequiredService<ChannelMoveTicketStore>().RevokeAccount(accountId);
        if (revoked > 0)
            logger.LogWarning("Session {SessionId}: account {AccountId} logged out with {Count} channel move(s) "
                + "pending; revoked", session.SessionId, accountId, revoked);
        try
        {
            var registry = services.GetRequiredService<IOnlineAccountRegistry>();
            var broadcast = services.GetRequiredService<BroadcastService>();
            var entry = await registry.GetAsync(accountId);
            if (entry is null) return;
            var owners = LogoutReleasableOwners(entry.Owner, session.SessionId,
                id => broadcast.GetSession(id) is { IsConnected: true });
            await registry.ReleaseAnyAsync(accountId, owners);
            logger.LogInformation("Session {SessionId}: account {AccountId} logged out; online entry released",
                session.SessionId, accountId);
        }
        catch (Exception ex)
        {
            // The entry then goes the old way: when this connection closes and the orphan wait runs out, or
            // after OnlineAccountRules.StaleAfter once nothing refreshes it.
            logger.LogWarning(ex, "Session {SessionId}: could not release the online entry of account {AccountId} at logout",
                session.SessionId, accountId);
        }
    }

    /// <summary>
    /// The owners a 1302 on <paramref name="logoutSessionId"/> may remove the account's entry from: always
    /// its own connection, and also <paramref name="holder"/> when that is a connection of this process that
    /// has closed, i.e. the one a channel move came from, whose hold loop has not yet handed the entry over
    /// (it looks once a second). A holder that is still connected is another client of the account that
    /// claimed it with its own 1303 (only possible with Login.AllowDuplicateLogin) and keeps it; so does a
    /// holder of another process.
    /// </summary>
    internal static IReadOnlyCollection<string> LogoutReleasableOwners(string holder, uint logoutSessionId,
        Func<uint, bool> isConnected)
    {
        var own = OnlineAccountRules.OwnerFor(logoutSessionId);
        return holder != own
               && OnlineAccountRules.TryGetLocalSessionId(holder, out var held)
               && held != logoutSessionId && !isConnected(held)
            ? [own, holder]
            : [own];
    }

    /// <summary>
    /// Keeps the account's <see cref="IOnlineAccountRegistry"/> entry alive while the account is in this
    /// Zone, removes it when it leaves, and drops the connection that holds it when the Login server asks
    /// (another 12002 for the same account). A 1302 logout removes the entry itself
    /// (<see cref="ReleaseOnlineAccountAtLogoutAsync"/>); the next heartbeat then finds none and the loop ends.
    /// </summary>
    /// <remarks>
    /// "In this Zone" is not "this connection is open". A 1420 channel move closes the connection about
    /// 2 ms after 2556 and the client comes back on a new one that authenticates with 1421 and never sends a
    /// 1303 (ChannelMoveLoginHandler), so the 1303 claim would otherwise end at the
    /// first channel move and the account would look offline for the rest of its stay. When the connection
    /// closes, the entry is kept alive and handed to the connection the account comes back on
    /// (<see cref="FindSuccessorSessionId"/>, <see cref="IOnlineAccountRegistry.TransferAsync"/>); it is
    /// released only when none turns up in time (<see cref="SuccessorWaitOver"/>). A kick requested while
    /// no connection holds the account shortens that wait to <see cref="SuccessorSettle"/>: the new login's
    /// retry gets in after it, and a mover that lands within it is the one dropped (the older connection loses).
    /// </remarks>
    private static async Task HoldOnlineAccountAsync(ProudSession session, IOnlineAccountRegistry registry,
        uint accountId, string owner, PlayerStateManager players, BroadcastService broadcast,
        ChannelMoveTicketStore tickets, ILogger logger, CancellationToken loopCt)
    {
        Interlocked.Increment(ref _activeHoldLoops);
        try
        {
            await HoldOnlineAccountLoopAsync(session, registry, accountId, owner, players, broadcast, tickets, logger, loopCt);
        }
        finally
        {
            Interlocked.Decrement(ref _activeHoldLoops);
        }
    }

    private static async Task HoldOnlineAccountLoopAsync(ProudSession session, IOnlineAccountRegistry registry,
        uint accountId, string owner, PlayerStateManager players, BroadcastService broadcast,
        ChannelMoveTicketStore tickets, ILogger logger, CancellationToken loopCt)
    {
        DateTime? orphanedAt = null;  // when the connection holding the account closed
        DateTime? settleFrom = null;  // first tick after that with no move pending, or with a kick asked
        while (true)
        {
            // Cancelled only when a newer 1303 of the same connection started its own loop: that one owns the
            // entry now, so this one ends without releasing it.
            try { await Task.Delay(OnlineAccountRules.HeartbeatInterval, loopCt); }
            catch (OperationCanceledException) { return; }
            try
            {
                var now = DateTime.UtcNow;
                if (session.State == SessionState.Disconnected || !session.IsConnected)
                {
                    var successorId = FindSuccessorSessionId(players.GetAllPlayers(), accountId, session.SessionId,
                        id => broadcast.GetSession(id) is { IsConnected: true });
                    var successor = successorId is { } sid ? broadcast.GetSession(sid) : null;
                    if (successor is not null)
                    {
                        var next = OnlineAccountRules.OwnerFor(successor.SessionId);
                        // False: a 1303 of the same account claimed the entry meanwhile and holds it now.
                        if (!await registry.TransferAsync(accountId, owner, next)) return;
                        logger.LogInformation("Account {AccountId}: online entry moved from session {From} to session {To} "
                            + "(channel move)", accountId, session.SessionId, successor.SessionId);
                        session = successor;
                        owner = next;
                        orphanedAt = null;
                        settleFrom = null;
                    }
                    else
                    {
                        orphanedAt ??= now;
                        if (settleFrom is null && tickets.PendingCount == 0) settleFrom = now;
                        if (SuccessorWaitOver(orphanedAt.Value, settleFrom, now))
                        {
                            await registry.ReleaseAsync(accountId, owner);
                            return;
                        }
                    }
                }

                // Null: a later connection of the same account holds the entry now (it claimed at its own
                // 1303), so this one has nothing left to keep. It is not dropped for that.
                var entry = await registry.HeartbeatAsync(accountId, owner);
                if (entry is null) return;
                if (entry.KickRequestedAt is null) continue;

                if (orphanedAt is not null)
                {
                    // No open connection to send the 2305 to: wait only the settle from now (see remarks).
                    settleFrom ??= now;
                    continue;
                }

                logger.LogInformation("Session {SessionId}: account {AccountId} logged in again elsewhere; "
                    + "sending 2305 reason {Reason} and closing", session.SessionId, accountId, DuplicateLoginReason);
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnCharacterDisconnectNotify, BuildDuplicateLoginNotify2305());
                await registry.ReleaseAsync(accountId, owner);
                await Task.Delay(DuplicateLoginGrace);
                if (session.IsConnected) await session.DisconnectAsync();
                return;
            }
            catch (Exception ex)
            {
                // A failed round trip leaves the entry un-refreshed; after OnlineAccountRules.StaleAfter
                // the Login server treats it as gone, so an outage never locks the account out.
                logger.LogWarning(ex, "Session {SessionId}: online-account heartbeat failed for account {AccountId}",
                    session.SessionId, accountId);
            }
        }
    }

    /// <summary>
    /// Send zone character list (RMI 2307) with full per-character data.
    /// Format: [data_length:4][accountId:4][charCount:2][104-byte header + equipment per char]
    /// Example: 7065 bytes for 6 characters.
    /// </summary>
    internal static async Task SendZoneCharacterListAsync(
        ProudSession session,
        IServiceProvider services,
        ILogger logger,
        CancellationToken ct)
    {
        var characters = services.GetRequiredService<ICharacterRepository>();
        var charList = session.AccountId != null
            ? await characters.GetCharactersByAccountAsync(session.AccountId.Value)
            : [];

        // Each character's worn equipment (StorageType==1) rides the 2307 equipment vector as 126-byte
        // records so the char-select preview — and the in-world model, which inherits that CKharaMgr
        // appearance state — renders the gear. See CharacterSerializer.WriteCharacterList.
        var equipByChar = new Dictionary<uint, IReadOnlyList<RO2.Inventory.Domain.InventoryItem>>();
        if (charList.Count > 0)
        {
            var inventory = services.GetRequiredService<IInventoryRepository>();
            foreach (var c in charList)
                equipByChar[c.Id] = await inventory.GetEquipmentAsync(c.Id);
        }

        var listAccountId = session.AccountId ?? 1;
        await session.SendRmiAsync(ZoneS2COpcodes.ZoneCharacterList, writer =>
        {
            CharacterSerializer.WriteCharacterList(writer, listAccountId, charList, equipByChar);
        });

        // OnPreOccupyCharacterNameACK (UNENC 2820) — character name reservation, right after the charlist.
        // Format: [u32 len=6][u32 accountId][u16 0]. The u32 is the ACCOUNT id, not a character id: it equals the
        // 12502 login account and the accountId of the 2307 it follows; len is 6 and the u16 is zero. It used to carry
        // charList[0].Id. Uses the same value the 2307 above was built with, so the two always agree.
        // When it goes out: see ShouldSendPreOccupyAck.
        var listed = charList.Select(c => c.Id).ToHashSet();
        bool sendPreOccupy = ShouldSendPreOccupyAck(LastListedCharacters.TryGetValue(session, out var previous) ? previous.Value : null, listed);
        LastListedCharacters.AddOrUpdate(session, new StrongBox<HashSet<uint>>(listed));
        if (sendPreOccupy)
        {
            using var tw = new RO2.Network.Packets.PacketWriter();
            tw.WriteUInt32(6);
            tw.WriteUInt32(listAccountId);
            tw.WriteUInt16(0);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPreOccupyCharacterNameACK, tw.GetWrittenMemory());
        }

        logger.LogInformation("Session {SessionId}: ZoneCharacterList (RMI 2307) sent with {Count} character(s){PreOccupy}",
            session.SessionId, charList.Count, sendPreOccupy ? " + 2820" : "");
    }

    /// <summary>
    /// The characters of the last whole list this connection was sent, tied to the session object so it
    /// goes away with the connection. Read and written only by <see cref="SendZoneCharacterListAsync"/>.
    /// </summary>
    private static readonly ConditionalWeakTable<ProudSession, StrongBox<HashSet<uint>>> LastListedCharacters = new();

    /// <summary>
    /// Most characters a 2307 can list and still be followed by a 2820. The cause is unknown: see
    /// <see cref="ShouldSendPreOccupyAck"/>. It counts the characters in the list, not on the account.
    /// </summary>
    internal const int PreOccupyMaxCharacters = 6;

    /// <summary>
    /// Whether a whole 2307 goes out with its 2820, given the characters of the previous list on the same
    /// connection (<c>null</c> when this is the connection's first) and those of this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This rule fits the observed behaviour of the original server, whose own condition is not known. Each 2307 is
    /// labelled by the list the same connection got before it (the first list after a 1303 has none):
    /// </para>
    /// <list type="bullet">
    /// <item>A list whose characters differ from the previous one (the re-list after a 1415 create or a
    /// 1577 delete) gets no 2820. The old code sent it after all of them that were not empty.</item>
    /// <item>The first list after an account's first character (previous list empty) gets one.</item>
    /// <item>The connection's first list, and an unchanged re-list (back from the world through 1418), get
    /// one when they list at most <see cref="PreOccupyMaxCharacters"/> characters. Zero-character first lists
    /// get one too (the old code sent none). Accounts with many characters (for example 10) get none. The
    /// exceptions depend on per-account state this server does not have, not on the list's content.</item>
    /// </list>
    /// <para>
    /// The 2820 only makes the client copy the account id and the empty name into its login state, so an extra or
    /// missing 2820 changes nothing on screen.
    /// </para>
    /// </remarks>
    internal static bool ShouldSendPreOccupyAck(IReadOnlySet<uint>? previous, IReadOnlySet<uint> current)
        => current.Count <= PreOccupyMaxCharacters
           && (previous is null || previous.Count == 0 || previous.SetEquals(current));
}
