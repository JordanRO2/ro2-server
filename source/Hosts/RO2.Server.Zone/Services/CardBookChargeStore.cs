using System.Buffers.Binary;
using RO2.Application.Interfaces;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Zone-side handling of the per-character card-book charge bought with card albums: the wire form and the UTC to server-local conversion. Persistence is provided by <see cref="ICardBookChargeRepository"/>.
/// </summary>
/// <remarks>
/// The wire carries server local time: the expiry is an OLE date on the local clock, and a re-use extends it by the item's duration in days. Only the stored value is UTC.
/// </remarks>
public static class CardBookChargeStore
{
 /// <summary>State value for a charged book. An expiry follows.</summary>
    public const ushort StateCharged = 1;

 /// <summary>State value for a book whose charge has lapsed. The expiry is always 0.0.</summary>
    public const ushort StateLapsed = 3;

 /// <summary>Converts a stored UTC expiry to server local time, the clock used by 2793 and the lapse rule.</summary>
    public static DateTime ToServerLocal(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

 /// <summary>
 /// Builds the 2793 NfyCardBookChargedTime body: a u16 state followed by an f32 OLE-automation expiry.
 /// </summary>
 /// <remarks>
 /// Charged (<paramref name="chargedUntil"/> later than <paramref name="now"/>, both in server local time): state 1 with the expiry. Lapsed: state 3 with 0.0. Callers send nothing when <see cref="ICardBookChargeRepository.GetChargedUntilUtcAsync"/> returns null.
 /// </remarks>
 /// <param name="chargedUntil">Expiry of the charge, in server local time.</param>
 /// <param name="now">Current server local time.</param>
    public static byte[] Build2793(DateTime chargedUntil, DateTime now)
    {
        var body = new byte[6];
        bool charged = chargedUntil > now;
        BinaryPrimitives.WriteUInt16LittleEndian(body, charged ? StateCharged : StateLapsed);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(2), charged ? (float)chargedUntil.ToOADate() : 0f);
        return body;
    }
}
