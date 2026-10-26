namespace RO2.Characters.Application;

/// <summary>
/// Encodes and decodes the character create/appearance block, so the appearance flow does not depend on the
/// host's serializer or wire-layout constants. Backed by the host's CharacterSerializer and appearance packets.
/// </summary>
public interface IAppearanceCodec
{
    /// <summary>Parses the stored appearance JSON into its raw create block (or null) and race.</summary>
    (byte[]? RawBytes, byte Race) ParseStored(string? appearanceJson);

    /// <summary>Overlays <paramref name="request"/> onto the stored create block, pinning create[0] to <paramref name="gender"/>.</summary>
    byte[] ApplyToCreateBlock(byte[]? storedRaw, byte gender, byte[] request);

    /// <summary>The gender byte of a stored create block, or <paramref name="raceFallback"/> when the block is too short.</summary>
    byte StoredGender(byte[]? rawBytes, byte raceFallback);

    /// <summary>The gender byte of a create block.</summary>
    byte GenderByte(byte[] createBlock);

    /// <summary>Re-derives the stored appearance JSON and the gender/race from a create block.</summary>
    (string Json, byte Gender, byte Race) Derive(byte[] newRaw);
}
