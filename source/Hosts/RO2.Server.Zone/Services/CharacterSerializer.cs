using System.Text;
using System.Text.Json;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Serializes <see cref="Character"/> entities into the character list format expected by the client.
/// </summary>
/// <remarks>
/// Layout of the list payload: [u32 payload length][u32 account id][u16 character count], then for each character a
/// fixed 102-byte header followed by its worn-equipment vector ([u16 count][count x 126-byte item record]).
/// </remarks>
public static class CharacterSerializer
{
    /// <summary>Size in bytes of one character header in the list.</summary>
    private const int CharacterHeaderSize = 102;

    /// <summary>Length of the name field in UTF-16 characters (42 bytes, zero padded).</summary>
    private const int NameCharCount = 21;

    /// <summary>High 32 bits of the 64-bit character uid; the low 32 bits are the character id.</summary>
    private const uint UidHighBits = 0x0E043DA7;

    /// <summary>Header offset 62: the shade byte the creation payload carries at index 12 and the list echoes back.</summary>
    private const byte ShadeByte = 0xFF;

    /// <summary>Map written in the header for a character that has none stored (map 1100, Prontera).</summary>
    private const uint FallbackMapId = 1100;

    /// <summary>Header offset 91: delete flag value for a character that is not being deleted.</summary>
    private const byte NotDeleting = 0;

    /// <summary>Header offsets 92-99: delete timer value (int64) for a character that is not being deleted.</summary>
    private const long DeleteTimerNone = -1;

    /// <summary>Header offsets 77-88: size of the profession (ProJob) progress block, all zero while no profession is modelled.</summary>
    private const int ProfessionBlockSize = 12;

    /// <summary>Size in bytes of the appearance block copied from the creation payload (header offsets 50-72).</summary>
    private const int AppearanceBlockSize = 23;

    // Indexes into the appearance bytes of the creation payload.
    private const int CreateGender = 0;
    private const int CreateRace = 1;
    private const int CreateHairIndex = 2;
    private const int CreateHairType = 3;
    private const int CreateShade = 4;
    private const int CreateFace = 6;
    private const int CreateBodyType = 10;
    private const int CreateSkinColor = 11;
    private const int CreateHairStyle = 14;
    private const int CreateHairColor = 15;
    private const int CreateCombatClass = 19;
    private const int CreateProfession = 21;

 /// <summary>
 /// Writes the full character list payload: the account id, the character count, then one header per character.
 /// </summary>
    public static void WriteCharacterList(PacketWriter writer, uint accountId, IReadOnlyList<Character> characters,
        IReadOnlyDictionary<uint, IReadOnlyList<InventoryItem>>? equipmentByChar = null)
    {
        var lengthPos = writer.Reserve(4);
        var dataStart = writer.Length;

        writer.WriteUInt32(accountId);
        writer.WriteUInt16((ushort)characters.Count);

        foreach (var character in characters)
        {
            WriteCharacterHeader(writer, character);

            // Worn-equipment vector: [u16 count][count x 126-byte item record], the same record as the
            // world-entry item list. The client dresses the character-select preview from these records and keeps
            // that appearance for the in-world model, so an empty vector renders the character unequipped.
            IReadOnlyList<InventoryItem>? worn = null;
            equipmentByChar?.TryGetValue(character.Id, out worn);
            int equipCount = worn?.Count ?? 0;
            writer.WriteUInt16((ushort)equipCount);
            if (worn != null)
                foreach (var item in worn)
                    writer.WriteBytes(WorldEntryService.BuildItemRecord126(item));
        }

        writer.WriteAt(lengthPos, (uint)(writer.Length - dataStart));
    }

 /// <summary>
 /// Writes a single 102-byte character header. The appearance bytes saved at character creation are written first, followed by the server-derived fields.
 /// </summary>
 /// <remarks>
 /// Header layout (byte offsets): 0-7 uid, 8-49 name, 50-72 appearance, 73 creation profession, 74 job level,
 /// 75-76 equipment hide flag, 77-88 profession block, 89-90 unspent bonus stat points, 91 delete flag,
 /// 92-99 delete timer, 100 profession level, 101 profession id.
 /// </remarks>
    private static void WriteCharacterHeader(PacketWriter writer, Character character)
    {
        var appearance = ParseAppearance(character.AppearanceJson);
        var startPos = writer.Length;

        // [0-7] character uid
        writer.WriteUInt64(GenerateUid(character.Id));

        // [8-49] name, UTF-16 zero-padded to 21 characters
        WriteWideStringFixed(writer, character.Name, NameCharCount);

        // [50-72] appearance. Offsets 50-64 repeat creation payload bytes 0-14 one to one; the client reads the
        // hair at 52, the face at 56 and the skin and eye colour at 60 as 32-bit appearance values.
        writer.WriteByte(character.Gender);          // [50] = create[0]  gender
        writer.WriteByte(appearance.Race);           // [51] = create[1]  race
        writer.WriteByte(appearance.Job);            // [52] = create[2]  hair index (stored raw, not the mapped job)
        writer.WriteByte(appearance.UnknownByte35);  // [53] = create[3]  hair type
        writer.WriteByte(appearance.UnknownByte36);  // [54] = create[4]  shade
        writer.WriteByte(0);                         // [55] = create[5]  padding
        writer.WriteByte(appearance.Face);           // [56] = create[6]  face
        writer.WriteZeros(3);                        // [57-59] = create[7-9] padding
        writer.WriteByte(appearance.BodyType);       // [60] = create[10] body type
        writer.WriteByte(appearance.SkinColor);      // [61] = create[11] skin colour
        writer.WriteByte(ShadeByte);                 // [62] = create[12] shade
        writer.WriteByte(0);                         // [63] = create[13] padding
        writer.WriteByte(appearance.HairStyle);      // [64] = create[14] hair style
        // [65] combat class (JobInfo id). It is the same byte the world-entry character record carries, so both
        // come from WorldEntryService.CombatJobByte; it follows a quest job change, unlike the class picked at creation.
        writer.WriteByte(WorldEntryService.CombatJobByte(character)); // [65] combat class (JobInfo.ID)
        writer.WriteByte(character.Level);           // [66] level
        writer.WriteUInt32(character.MapId != 0 ? character.MapId : FallbackMapId); // [67-70] map id
        writer.WriteZeros(2);                        // [71-72] padding

        // [73] creation profession, a ProJob_Type id; the same byte the world-entry character record carries
        // (WorldEntryService.CreationProfessionByte).
        writer.WriteByte(WorldEntryService.CreationProfessionByte(character));
        // [74] job level (not the base level): the "<Job> Lv N" the character-select shows.
        writer.WriteByte(character.JobLevel);
        // [75-76] equipment hide flag: the per-slot "hide equipment" bits, toggled by the client's costume-flag
        // request. An untouched character carries Character.DefaultEquipHideFlag.
        writer.WriteUInt16(character.EquipHideFlag);
        // [77-88] profession (ProJob) progress block. No profession progress is stored, so it is all zero.
        writer.WriteZeros(ProfessionBlockSize);
        // [89-90] unspent bonus stat points, derived the same way as the pool in the character stats message so
        // the list and the sheet agree.
        writer.WriteUInt16(UnspentBonusStatPoints(character));
        // [91] delete flag
        writer.WriteByte(NotDeleting);
        // [92-99] delete timer (int64)
        writer.WriteInt64(DeleteTimerNone);
        // [100] profession level, [101] profession id (1 Chef, 2 Alchemist, 3 Blacksmith, 4 Artisan); both 0 for a
        // character without a profession. Written from WireProJob, the value the craft-list gate also reads.
        var proJob = WireProJob(character);
        writer.WriteByte(proJob.Level);
        writer.WriteByte(proJob.Id);

        var written = writer.Length - startPos;
        if (written != CharacterHeaderSize)
            throw new InvalidOperationException($"CharacterHeader expected {CharacterHeaderSize} bytes, wrote {written}");

    }

 /// <summary>
 /// Parses the appearance data from a character creation payload, keeping the raw appearance bytes for the character list.
 /// </summary>
    public static CharacterAppearance ParseCreateAppearance(PacketReader reader)
    {
        var appearance = new CharacterAppearance();

        // Keep the appearance bytes of the creation payload (up to 23) for replay in the character list.
        var rawLen = Math.Min(reader.Remaining, AppearanceBlockSize);
        if (rawLen > 0)
        {
            appearance.RawBytes = reader.ReadBytes(rawLen).ToArray();

            // Creation payload layout (byte index):
            //   [0] gender  [1] race  [2] hair index  [3] hair type  [4] shade  [5] padding  [6] face
            //   [7-9] padding  [10] body type  [11] skin colour  [12] shade  [13] padding  [14] hair style
            //   [15] hair colour  [16-18] padding  [19] combat class  [20] padding  [21] creation profession
            //   [22] padding
            // [2] is the hair index, not the job. [19] is the combat class (a JobInfo id) and [21] the profession
            // picked at creation (a ProJob_Type id); they are independent values. The profession level and id the
            // list shows at header offsets 100-101 are not part of the creation payload and start at 0.
            if (rawLen > CreateGender) appearance.Gender = appearance.RawBytes[CreateGender];
            if (rawLen > CreateRace) appearance.Race = appearance.RawBytes[CreateRace];
            if (rawLen > CreateHairIndex) appearance.Job = appearance.RawBytes[CreateHairIndex];             // hair index (goes to header offset 52)
            if (rawLen > CreateHairType) appearance.UnknownByte35 = appearance.RawBytes[CreateHairType];
            if (rawLen > CreateShade) appearance.UnknownByte36 = appearance.RawBytes[CreateShade];
            if (rawLen > CreateFace) appearance.Face = appearance.RawBytes[CreateFace];
            if (rawLen > CreateBodyType) appearance.BodyType = appearance.RawBytes[CreateBodyType];
            if (rawLen > CreateSkinColor) appearance.SkinColor = appearance.RawBytes[CreateSkinColor];
            if (rawLen > CreateHairStyle) appearance.HairStyle = appearance.RawBytes[CreateHairStyle];
            if (rawLen > CreateHairColor) appearance.HairColor = appearance.RawBytes[CreateHairColor];
            // The combat class is stored in Character.Job by the character-create handler.
            if (rawLen > CreateCombatClass) appearance.EyeColor = appearance.RawBytes[CreateCombatClass];
            // The creation profession (a ProJob_Type id, not a class).
            if (rawLen > CreateProfession) appearance.BaseClass = appearance.RawBytes[CreateProfession];
        }

        return appearance;
    }

    public static string ToJson(CharacterAppearance appearance)
    {
        return JsonSerializer.Serialize(appearance, AppearanceJsonContext.Default.CharacterAppearance);
    }

 /// <summary>Parses appearance data from its stored JSON form.</summary>
    public static CharacterAppearance ParseAppearancePublic(string? json) => ParseAppearance(json);

    private static CharacterAppearance ParseAppearance(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new CharacterAppearance();
        try
        {
            return JsonSerializer.Deserialize(json, AppearanceJsonContext.Default.CharacterAppearance)
                   ?? new CharacterAppearance();
        }
        catch { return new CharacterAppearance(); }
    }

 /// <summary>
 /// Returns the profession (ProJob) level and id sent to the client, at character-list offsets 100-101 and in the world-entry character record. Logic that depends on whether a character has a profession must use this value, not <see cref="Character.LifeJob"/>, so the server never acts on a profession the client was not told about.
 /// </summary>
 /// <remarks>
 /// Currently always (0, 0): professions are not modelled yet, and <see cref="Character.LifeJob"/> is not reliable on stored rows. When professions are implemented, derive both bytes here.
 /// </remarks>
    internal static (byte Level, byte Id) WireProJob(Character character) => (0, 0);

    private static ushort UnspentBonusStatPoints(Character character)
    {
        int earned = StatService.BonusStatPointsEarned(character.Level);
        int spent = character.BonusStr + character.BonusAgi + character.BonusInt
                  + character.BonusWis + character.BonusVit + character.BonusLuk;
        return (ushort)Math.Clamp(earned - spent, 0, ushort.MaxValue);
    }

 /// <summary>
 /// Returns the 64-bit id (GID) of <paramref name="characterId"/> as the client sees it: the uid of the character-list header (offsets 0-7), which the client logs in with and which comes back as the character uid. The guild roster uses the same value, because the client finds its own roster record by it.
 /// </summary>
    internal static ulong GenerateUid(uint characterId)
    {
        return ((ulong)UidHighBits << 32) | characterId;
    }

    private static void WriteWideStringFixed(PacketWriter writer, string value, int charCount)
    {
        var maxBytes = charCount * 2;
        var bytes = Encoding.Unicode.GetBytes(value ?? "");
        var bytesToWrite = Math.Min(bytes.Length, maxBytes - 2);
        writer.WriteBytes(bytes.AsSpan(0, bytesToWrite));
        writer.WriteZeros(maxBytes - bytesToWrite);
    }
}

/// <summary>Appearance data of a character, taken from the creation payload.</summary>
public class CharacterAppearance
{
    public byte Gender { get; set; }
    public byte Race { get; set; } = 1;
    public byte Job { get; set; }
    public byte UnknownByte35 { get; set; }
    public byte UnknownByte36 { get; set; }
    public byte Face { get; set; } = 1;
    public byte SkinColor { get; set; } = 1;
    public byte HairStyle { get; set; } = 1;
    public byte HairColor { get; set; }
    public byte BodyType { get; set; }
 /// <summary>Combat class picked at creation (a <c>JobInfo</c> id). The name is kept because it is the stored JSON key.</summary>
    public byte EyeColor { get; set; }
 /// <summary>Profession picked at creation (a <c>ProJob_Type</c> id). The name is kept because it is the stored JSON key.</summary>
    public byte BaseClass { get; set; }
    public uint[] Sliders { get; set; } = new uint[5];
 /// <summary>Raw 23 appearance bytes from the creation payload, written directly into the character list.</summary>
    public byte[]? RawBytes { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(CharacterAppearance))]
internal partial class AppearanceJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
