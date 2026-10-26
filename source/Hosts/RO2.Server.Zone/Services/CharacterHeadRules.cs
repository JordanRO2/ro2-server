using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Character head/creation helpers split out of <see cref="WorldEntryService"/> (B5 Phase-0, S5): facing, the bonus-stat
/// block, job-mastery, the combat class / creation profession read from the stored creation block, and the two creation-row
/// fixups. Character-owned host helpers, shared by the 2310 head, the stat sheet, PC-save, player-death and trait builders.
/// </summary>
internal static class CharacterHeadRules
{
 /// <summary>Combat class (job id) sent for a character that has none.</summary>
    private const byte NoClassFallbackJob = 0x01;

 /// <summary>Offset of the combat class picked at creation, inside the stored 23-byte creation block. The 2310 head carries it at HeadOffset.CombatClass.</summary>
    private const int CreateClassOffset = 19;
 /// <summary>Offset of the profession picked at creation, inside the stored 23-byte creation block. The 2310 head carries it at HeadOffset.CreationProfession.</summary>
    private const int CreateProfessionOffset = 21;

 /// <summary>Facing byte of the 2310 head: one step is this many degrees.</summary>
    private const double FacingStepDegrees = 2.0;
 /// <summary>Facing byte of the 2310 head: the number of steps in a full turn.</summary>
    private const int FacingStepCount = 180;

 /// <summary>Length of the creation block that is kept verbatim from character creation.</summary>
    private const int CreateBlockLength = 23;

 /// <summary>
 /// Converts a facing yaw in radians to the facing byte of the 2310 head: 0 to 179 in 2-degree steps. A non-finite yaw gives 0.
 /// </summary>
    internal static byte FacingByte(float yawRadians)
    {
        if (!float.IsFinite(yawRadians))
            return 0;
        double deg = yawRadians * (180.0 / Math.PI) % 360.0;
        if (deg < 0) deg += 360.0;
        return (byte)((int)Math.Round(deg / FacingStepDegrees, MidpointRounding.AwayFromZero) % FacingStepCount);
    }

 /// <summary>
 /// The allocated bonus STR, AGI, INT, WIS and VIT, and the unspent bonus points. Shared by the stat sheet and the character head so they agree.
 /// </summary>
    internal static (ushort[] Bonus5, ushort Unspent) BonusStatBlock(Character character)
    {
        int earned = CharacterStats.BonusStatPointsEarned(character.Level);
        int spent = character.BonusStr + character.BonusAgi + character.BonusInt
                  + character.BonusWis + character.BonusVit + character.BonusLuk;
        var unspent = (ushort)Math.Clamp(earned - spent, 0, ushort.MaxValue);
        var bonus5 = new ushort[]
        {
            (ushort)Math.Clamp(character.BonusStr, 0, ushort.MaxValue),
            (ushort)Math.Clamp(character.BonusAgi, 0, ushort.MaxValue),
            (ushort)Math.Clamp(character.BonusInt, 0, ushort.MaxValue),
            (ushort)Math.Clamp(character.BonusWis, 0, ushort.MaxValue),
            (ushort)Math.Clamp(character.BonusVit, 0, ushort.MaxValue),
        };
        return (bonus5, unspent);
    }

 /// <summary>
 /// The character's job-mastery point total. Always 0, because the server does not store mastery points yet.
 /// </summary>
 /// <remarks>
 /// When mastery points are persisted, return them here. The total is shared with the job-mastery point change message, so both must read it from this method.
 /// </remarks>
    internal static ulong JobMasteryPoints(Character? character) => 0;

 /// <summary>
 /// Returns the combat class of the character: its job id, as the client reads it for the class name and job requirements.
 /// </summary>
 /// <remarks>
 /// Before any job change this is the class chosen at creation. After a job change it is the new class stored on the character.
 /// A zero is never sent. A character with no class sends the previous default.
 /// </remarks>
    internal static byte CombatJobByte(Character character)
    {
        var create = CreateBlock(character);
        if (create != null && create[CreateClassOffset] != 0 && character.Job == create[CreateProfessionOffset])
            return create[CreateClassOffset];
        return character.Job != 0 ? character.Job : NoClassFallbackJob;
    }

 /// <summary>
 /// Corrects the job of a character row stored before the creation class was kept separately.
 /// </summary>
 /// <remarks>
 /// Changes the stored job only when it still equals the creation profession and the stored creation class differs from it.
 /// Returns true when the row changed, so the caller can persist it. Runs at most once per row.
 /// </remarks>
    internal static bool TryHealStoredCombatClass(Character character)
    {
        var create = CreateBlock(character);
        if (create is null) return false;
        byte cls = create[CreateClassOffset], profession = create[CreateProfessionOffset];
        if (cls == 0 || cls == profession || character.Job != profession) return false;
        character.Job = cls;
        return true;
    }

 /// <summary>
 /// Returns the profession the character picked at creation, which the client reads from the character head.
 /// </summary>
 /// <remarks>
 /// A job change does not alter it. The value is read from the stored creation block, or from the character's job when there is none.
 /// </remarks>
    internal static byte CreationProfessionByte(Character character)
    {
        var create = CreateBlock(character);
        return create != null && create[CreateProfessionOffset] != 0 ? create[CreateProfessionOffset] : character.Job;
    }

 /// <summary>
 /// Sets the character's creation profession in the stored creation block. Returns the previous appearance data so a caller can restore it if its save fails.
 /// </summary>
 /// <remarks>
 /// Call <see cref="TryHealStoredCombatClass"/> first, so that the class is not read from the profession field of an older row.
 /// A stored block shorter than 23 bytes is padded with zeros.
 /// </remarks>
    internal static string? SetCreationProfession(Character character, byte profession)
    {
        var previous = character.AppearanceJson;
        var ap = CharacterSerializer.ParseAppearancePublic(previous);
        var raw = ap.RawBytes ?? [];
        if (raw.Length <= CreateProfessionOffset)
        {
            var padded = new byte[CreateBlockLength];
            Buffer.BlockCopy(raw, 0, padded, 0, raw.Length);
            raw = padded;
        }
        else
        {
            raw = (byte[])raw.Clone();
        }
        raw[CreateProfessionOffset] = profession;
        ap.RawBytes = raw;
        ap.BaseClass = profession;
        character.AppearanceJson = CharacterSerializer.ToJson(ap);
        return previous;
    }

 /// <summary>
 /// The stored creation block when it holds the profession byte, otherwise null. Rows made by character creation always carry all 23 bytes.
 /// </summary>
    private static byte[]? CreateBlock(Character character)
    {
        var raw = CharacterSerializer.ParseAppearancePublic(character.AppearanceJson).RawBytes;
        return raw is { Length: > CreateProfessionOffset } ? raw : null;
    }
}
