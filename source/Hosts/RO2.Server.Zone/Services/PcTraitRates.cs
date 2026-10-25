using System.Buffers.Binary;
using static RO2.Characters.Domain.TraitRates;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The six rate slots of the 2498 (OnPcTrait) blob, in 1/100 % (u16): cast speed (+58), critical (+60), hit (+62),
/// vigor (+84), acceleration (+86) and penetration (+130). Four of them are derived from the character's level
/// and the kind-3 effect lines of its traits.
/// </summary>
internal static class PcTraitRates
{
 /// <summary>Offset of the cast speed rate in the 2498 blob.</summary>
    internal const int CastRateOffset = 58;

 /// <summary>Offset of the critical rate in the 2498 blob.</summary>
    internal const int CritRateOffset = 60;

 /// <summary>Offset of the hit rate in the 2498 blob.</summary>
    internal const int HitRateOffset = 62;

 /// <summary>Offset of the vigor rate in the 2498 blob.</summary>
    internal const int VigorRateOffset = 84;

 /// <summary>Offset of the acceleration rate in the 2498 blob.</summary>
    internal const int AccelRateOffset = 86;

 /// <summary>Offset of the penetration rate in the 2498 blob.</summary>
    internal const int PenRateOffset = 130;

 /// <summary>Offset of the cast speed constant in the 2498 blob.</summary>
    internal const int CastConstOffset = 132;

 /// <summary>Offset of the vigor constant in the 2498 blob.</summary>
    internal const int VigorConstOffset = 96;

 /// <summary>Offset of the acceleration constant in the 2498 blob.</summary>
    internal const int AccelConstOffset = 98;

 /// <summary>Offset of the penetration constant in the 2498 blob.</summary>
    internal const int PenConstOffset = 128;


 /// <summary>Ceiling of the vigor rate, 7500 (75 %).</summary>
    internal const int VigorRateCap = 7500;




 /// <summary>
 /// Writes the six rate slots of a 143-byte 2498 <paramref name="body"/> in place, for a character of
 /// <paramref name="level"/>, adding the flat effect lines of the character's traits.
 /// </summary>
    internal static void Stamp(byte[] body, int level, IReadOnlyDictionary<byte, double>? flatLines)
    {
        if (body.Length != TraitPackets.PcTraitBodyLen) return;
        const int Blob = TraitPackets.PcTraitBodyLen - TraitPackets.PcTraitBlobLen;   

        int Const(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(Blob + offset));
        int Line(byte trait)
            => flatLines is not null && flatLines.TryGetValue(trait, out var v)
                ? (int)Math.Clamp(Math.Round(v, MidpointRounding.ToEven), int.MinValue / 2, int.MaxValue / 2)
                : 0;
        void Put(int offset, long value)
            => BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(Blob + offset), (ushort)Math.Clamp(value, 0, ushort.MaxValue));

        Put(CastRateOffset, (long)Rate(Const(CastConstOffset), level) + Line(CastRateTrait));
        Put(CritRateOffset, Line(CritRateTrait));
        Put(HitRateOffset, Line(HitRateTrait));
        Put(VigorRateOffset, Math.Min(VigorRateCap, (long)Rate(Const(VigorConstOffset), level) + Line(VigorRateTrait)));
        Put(AccelRateOffset, (long)Rate(Const(AccelConstOffset), level) + Line(AccelRateTrait));
        Put(PenRateOffset, (long)Rate(Const(PenConstOffset), level) + Line(PenRateTrait));
    }
}
