using System.Collections.Concurrent;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builds the DualLife transform charge gauge (RMI 2529 OnNotifyTransChargeGague).
/// Compact-BA body: [compactLen][u16 10][u16 msgTag][u32 0][u16 seconds].
/// The client counts the charge down itself, so the value only has to be correct when it is sent.
/// </summary>
/// <remarks>
/// The charge starts when a transform ends. A character with no transform on record reports a full gauge.
/// The charge state is kept in memory per character.
/// </remarks>
public static class TransChargePackets
{
 /// <summary>msgTag of 2529.</summary>
    private const ushort TagOnNotifyTransChargeGague = 58403; 

 /// <summary>Full-charge time in seconds (1800).</summary>
    public const int ChargeTimeSeconds = 1800;

 /// <summary>Duration of a transform in seconds (30).</summary>
    public const int TransModeTimeSeconds = 30;

 /// <summary>Character id to the UTC instant at which its charge starts to accrue.</summary>
    private static readonly ConcurrentDictionary<uint, DateTime> ChargeStartUtc = new();

 /// <summary>
 /// Records a transform start and returns the instant at which the charge begins to accrue.
 /// Returns false, recording nothing, while the character's previous transform is still running.
 /// </summary>
 /// <remarks>
 /// A repeated request is refused without changing the running transform.
 /// A start after the end replaces the older record.
 /// </remarks>
    public static bool TryRecordTransModeStart(uint characterId, DateTime nowUtc, out DateTime chargeStartUtc)
    {
        var next = nowUtc.AddSeconds(TransModeTimeSeconds);
        while (true)
        {
            if (ChargeStartUtc.TryGetValue(characterId, out var current))
            {
                if (current > nowUtc)
                {
                    chargeStartUtc = current;
                    return false;
                }
                if (ChargeStartUtc.TryUpdate(characterId, next, current))
                    break;
            }
            else if (ChargeStartUtc.TryAdd(characterId, next))
            {
                break;
            }
        }
        chargeStartUtc = next;
        return true;
    }

 /// <summary>True while the given charge start is the character's most recent transform.</summary>
    public static bool IsLatestTransform(uint characterId, DateTime chargeStartUtc)
        => ChargeStartUtc.TryGetValue(characterId, out var current) && current == chargeStartUtc;

 /// <summary>
 /// The gauge value to report now, in whole seconds, from 0 to <see cref="ChargeTimeSeconds"/>.
 /// It is 0 during a transform, and full for a character with no transform on record.
 /// </summary>
    public static ushort CurrentChargeSeconds(uint characterId, DateTime nowUtc)
    {
        if (!ChargeStartUtc.TryGetValue(characterId, out var chargeStart))
            return ChargeTimeSeconds;
        var elapsed = Math.Floor((nowUtc - chargeStart).TotalSeconds);
        return (ushort)Math.Clamp(elapsed, 0, ChargeTimeSeconds);
    }

 /// <summary>
 /// Builds the 2529 body for a gauge value in seconds.
 /// </summary>
    public static byte[] BuildTransChargeGauge2529(ushort seconds)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnNotifyTransChargeGague);
            p.WriteUInt32(0);
            p.WriteUInt16(Math.Min(seconds, (ushort)ChargeTimeSeconds));
        });
        return pw.ToArray();
    }
}
