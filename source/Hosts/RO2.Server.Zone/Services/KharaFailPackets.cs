using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2581 NfyKharaFailed: tells the holders of a party-death Khara that it has failed.
/// </summary>
/// <remarks>
/// Sent when a player dies, to the victim and to every party member who holds such a Khara. The body is a u32 Khara id
/// followed by a constant u16. No state changes on the server: the Khara stays accepted with its counts. The frame is
/// sent just before the victim's death-state update (2398).
/// </remarks>
public static class KharaFailPackets
{
 /// <summary>Fail_Type of the "without dying (whole party)" Khara rows.</summary>
    internal const byte FailTypeNoPartyDeath = 49;

 /// <summary>Constant u16 that follows the Khara id in the 2581 body. The client does not read it.</summary>
    internal const ushort FailedCode = 21714;

 /// <summary>Builds the 2581 NfyKharaFailed body: the Khara id and the constant u16.</summary>
    public static byte[] BuildNfyKharaFailed2581(uint kharaId)
    {
        var b = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(b, kharaId);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), FailedCode);
        return b;
    }

 /// <summary>Whether a player death fails this Khara.</summary>
    internal static bool FailsOnPartyDeath(KharaDefinition def)
        => def.Main.Fail_Type == FailTypeNoPartyDeath || def.Conditions.Any(c => c.Fail_Type == FailTypeNoPartyDeath);

 /// <summary>The Khara ids among <paramref name="activeIds"/> that a party death fails, in ascending order.</summary>
    internal static uint[] FailingKharas(IEnumerable<uint> activeIds, KharaCatalog catalog)
        => activeIds.Where(id => catalog.Get(id) is { } def && FailsOnPartyDeath(def)).Distinct().Order().ToArray();

 /// <summary>
 /// Sends the 2581 failure notices caused by a death of <paramref name="victim"/>, to the victim and to each party member,
 /// one per failing Khara held. Never throws: a failure here must not interrupt the death sequence.
 /// </summary>
 /// <param name="services">Provider of the <see cref="GameDataStore"/> and <see cref="PartyService"/> singletons. Without
 /// the store nothing is sent; without the party service only the victim is notified.</param>
    public static async Task SendDeathFailuresAsync(PlayerState victim, IServiceProvider services,
        BroadcastService broadcast, ILogger? log, CancellationToken ct = default)
    {
        try
        {
            if (services.GetService<GameDataStore>() is not { } store) return;
            var catalog = KharaCatalog.For(store);

            var receivers = new List<PlayerState> { victim };
            var parties = services.GetService<PartyService>();
            if (parties?.GetPlayerParty(victim.SessionId) is { } party)
            {
                foreach (var memberId in party.MemberSessionIds.ToArray())
                {
                    if (memberId == victim.SessionId) continue;
                    if (parties.GetOnlineMember(party, memberId) is { } member) receivers.Add(member);
                }
            }

            foreach (var receiver in receivers)
            {
                var failing = FailingKharas(KharaService.PublishedActiveIds(receiver.CharacterId), catalog);
                if (failing.Length == 0) continue;
                var session = broadcast.GetSession(receiver.SessionId);
                if (session is not { IsConnected: true }) continue;
                foreach (var kharaId in failing)
                    await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyKharaFailed, BuildNfyKharaFailed2581(kharaId), ct);
                log?.LogDebug("{Name}: {Count} Khara(s) failed by the death of {Victim} ({Ids})",
                    receiver.Name, failing.Length, victim.Name, string.Join(",", failing));
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Sending the Khara failures of {Victim}'s death failed", victim.Name);
        }
    }
}
