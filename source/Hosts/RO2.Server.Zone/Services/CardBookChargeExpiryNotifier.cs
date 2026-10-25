using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Sends the card-book charge expiry notice (2794) to a client whose charge runs out during its session.
/// </summary>
/// <remarks>
/// <para>
/// On receipt, the client clears its album alarm and greys out the card-book icon. It refuses to save a card until the charge is renewed. The body is empty: there is no envelope and no message tag.
/// </para>
/// <para>
/// A watch is armed when a charge is announced, at world entry or after an album use. It wakes at most every <see cref="CheckInterval"/>, stops when the session ends, and at expiry re-reads the stored expiry, so a charge extended in the meantime is followed to its new expiry. The lapse rule is the same as in <see cref="CardBookChargeStore.Build2793"/>.
/// </para>
/// <para>
/// The class is static and uses the singletons its callers already hold, so it needs no DI registration.
/// </para>
/// </remarks>
internal static class CardBookChargeExpiryNotifier
{
 /// <summary>Longest single wait before the watch checks the expiry again. Keeps every delay well under the Task.Delay limit.</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

 /// <summary>The 2794 body, which is empty.</summary>
    internal static ReadOnlyMemory<byte> Body => ReadOnlyMemory<byte>.Empty;

    private static readonly object Gate = new();
    private static readonly Dictionary<uint, Watch> Watches = new();

    private sealed class Watch(uint characterId)
    {
        public uint CharacterId { get; } = characterId;
        public CancellationTokenSource Cts { get; } = new();
    }

 /// <summary>
 /// Starts watching a character's charge on a session, and sends 2794 once when it lapses there.
 /// </summary>
 /// <remarks>
 /// A charge that has already lapsed needs no watch, because the 2793 that announced it already reported the lapse. Arming again replaces the existing watch.
 /// </remarks>
 /// <param name="session">Session to send the notice on.</param>
 /// <param name="characterId">Character whose charge is watched.</param>
 /// <param name="chargedUntil">Expiry of the charge that was just announced, in server local time.</param>
    public static void Arm(ProudSession session, uint characterId, DateTime chargedUntil,
        PlayerStateManager players, ICardBookChargeRepository charges, ILogger logger)
    {
        if (IsLapsed(chargedUntil, DateTime.Now)) return;

        var watch = new Watch(characterId);
        lock (Gate)
        {
            if (Watches.TryGetValue(session.SessionId, out var previous)) previous.Cts.Cancel();
            Watches[session.SessionId] = watch;
        }
        _ = Task.Run(() => RunAsync(session, watch, chargedUntil, players, charges, logger), CancellationToken.None);
    }

 /// <summary>
 /// Whether a charge has lapsed. An expiry at or before the current time counts as lapsed.
 /// </summary>
 /// <param name="chargedUntil">Expiry of the charge.</param>
 /// <param name="now">Current server local time.</param>
    internal static bool IsLapsed(DateTime chargedUntil, DateTime now) => chargedUntil <= now;

 /// <summary>
 /// Time to wait before checking again: until the expiry, but never longer than <see cref="CheckInterval"/>, and zero once the expiry is due.
 /// </summary>
 /// <param name="chargedUntil">Expiry of the charge.</param>
 /// <param name="now">Current server local time.</param>
    internal static TimeSpan NextWait(DateTime chargedUntil, DateTime now)
    {
        var left = chargedUntil - now;
        if (left <= TimeSpan.Zero) return TimeSpan.Zero;
        return left < CheckInterval ? left : CheckInterval;
    }

    private static async Task RunAsync(ProudSession session, Watch watch, DateTime chargedUntil,
        PlayerStateManager players, ICardBookChargeRepository charges, ILogger logger)
    {
        var ct = watch.Cts.Token;
        try
        {
            var until = chargedUntil;
            while (true)
            {
                var wait = NextWait(until, DateTime.Now);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct);

                if (!StillInWorld(session, watch.CharacterId, players)) return;
                if (!IsLapsed(until, DateTime.Now)) continue;

                
                var storedUtc = await charges.GetChargedUntilUtcAsync(watch.CharacterId, ct);
                if (storedUtc is null) return;
                var stored = CardBookChargeStore.ToServerLocal(storedUtc.Value);
                if (!IsLapsed(stored, DateTime.Now))
                {
                    until = stored;
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                await session.SendRmiAsync(ZoneS2COpcodes.NfyCardBookChargedTimeEnd, Body, ct);
                logger.LogInformation("Session {Id}: card-book charge of character {Char} lapsed at {Until:yyyy-MM-dd HH:mm}; sent 2794",
                    session.SessionId, watch.CharacterId, stored);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session {Id}: card-book charge watch of character {Char} failed",
                session.SessionId, watch.CharacterId);
        }
        finally
        {
            lock (Gate)
            {
                if (Watches.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, watch))
                    Watches.Remove(session.SessionId);
            }
        }
    }

    private static bool StillInWorld(ProudSession session, uint characterId, PlayerStateManager players) =>
        session.IsConnected
        && players.TryGetPlayer(session.SessionId, out var player)
        && player is not null
        && player.CharacterId == characterId;
}
