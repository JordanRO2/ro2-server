using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace RO2.Persistence.Mongo.Persistence;

/// <summary>
/// uint as Int32 when it fits and Int64 above int.MaxValue. The driver's default UInt32 serializer always writes
/// Int32 and throws OverflowException above int.MaxValue, which crashed saves of large char ids, dyes and ARGB colors.
/// Values that fit still land as Int32, so existing documents and stored shapes are unchanged.
/// </summary>
public sealed class LenientUInt32Serializer : StructSerializerBase<uint>
{
    public static readonly LenientUInt32Serializer Instance = new();

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, uint value)
    {
        if (value <= int.MaxValue) context.Writer.WriteInt32((int)value);
        else context.Writer.WriteInt64(value);
    }

    public override uint Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        return reader.CurrentBsonType switch
        {
            BsonType.Int32 => (uint)reader.ReadInt32(),
            BsonType.Int64 => ToUInt32(reader.ReadInt64()),
            var t => throw new FormatException($"Cannot read a uint from BSON type {t}."),
        };
    }

    // Surface an out-of-uint-range Int64 (only from a mis-schema of a former long/ulong field) instead of truncating.
    private static uint ToUInt32(long value)
    {
        if (value is < 0 or > uint.MaxValue)
            throw new FormatException($"Int64 value {value} is outside the uint range.");
        return (uint)value;
    }
}

/// <summary>Routes <c>uint</c> to <see cref="LenientUInt32Serializer"/>; providers are consulted before the driver's defaults.</summary>
public sealed class LenientNumericSerializationProvider : IBsonSerializationProvider
{
    public IBsonSerializer? GetSerializer(Type type) => type == typeof(uint) ? LenientUInt32Serializer.Instance : null;
}
