using RO2.Characters.Application;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="IAppearanceCodec"/> with the host's <see cref="CharacterSerializer"/> and appearance packets.</summary>
internal sealed class AppearanceCodec : IAppearanceCodec
{
    public (byte[]? RawBytes, byte Race) ParseStored(string? appearanceJson)
    {
        var a = CharacterSerializer.ParseAppearancePublic(appearanceJson);
        return (a.RawBytes, a.Race);
    }

    public byte[] ApplyToCreateBlock(byte[]? storedRaw, byte gender, byte[] request)
        => AppearanceChangePackets.ApplyToCreateBlock(storedRaw, gender, request);

    public byte StoredGender(byte[]? rawBytes, byte raceFallback)
        => rawBytes is { Length: > AppearanceChangePackets.GenderIndex } r
            ? r[AppearanceChangePackets.GenderIndex]
            : raceFallback;

    public byte GenderByte(byte[] createBlock) => createBlock[AppearanceChangePackets.GenderIndex];

    public (string Json, byte Gender, byte Race) Derive(byte[] newRaw)
    {
        var a = CharacterSerializer.ParseCreateAppearance(new PacketReader(newRaw));
        return (CharacterSerializer.ToJson(a), a.Gender, a.Race);
    }
}
