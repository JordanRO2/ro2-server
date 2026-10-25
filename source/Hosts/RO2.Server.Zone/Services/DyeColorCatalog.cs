using RO2.Economy.Domain;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Stores every dye colour as a BSON Int64. <see cref="Register"/> must run before any type that holds a dye colour is mapped.
/// </summary>
/// <remarks>
/// <para>
/// A dye colour is an ARGB value with alpha 0xFF and can reach 2^31 or more. The driver's default uint serializer
/// writes a checked Int32 and would throw on such values, so every dye colour member is registered with an Int64 serializer.
/// </para>
/// <para>
/// The convention applies to every <c>uint</c> member whose name contains <c>DyeColor</c>. Int64 values read back through
/// any uint serializer, so existing rows load unchanged.
/// </para>
/// <para>
/// A class is mapped once per process, so the convention must be registered before the first save or load of these types.
/// <see cref="Register"/> maps the stored types itself and verifies that each dye member received the Int64 serializer,
/// failing at start-up rather than on every later dye write.
/// </para>
/// </remarks>
public static class DyeColorBson
{
 /// <summary>Name fragment that identifies stored dye-colour members.</summary>
    private const string DyeMemberName = "DyeColor";

 /// <summary>Serializer used for every dye-colour member.</summary>
    private static readonly UInt32Serializer Int64Colour = new(BsonType.Int64);

 /// <summary>
 /// Types saved to MongoDB that carry a dye colour: the item row and the stores that hold item snapshots
 /// (auction articles, mail, the guild vault).
 /// </summary>
    internal static readonly Type[] StoredTypes =
        [typeof(InventoryItem), typeof(AuctionArticle), typeof(MailMessage), typeof(GuildStorageItem)];

    private static readonly Lock s_gate = new();
    private static bool s_conventionRegistered;
    private static bool s_verified;

 /// <summary>
 /// Registers the convention and maps <see cref="StoredTypes"/> with it. Safe to call more than once.
 /// </summary>
 /// <exception cref="InvalidOperationException">Thrown when a type was mapped before the first call.</exception>
    public static void Register()
    {
        lock (s_gate)
        {
            if (s_verified) return;
            if (!s_conventionRegistered)
            {
                RuntimeHelpers.RunClassConstructor(typeof(MongoDbContext).TypeHandle);
                ConventionRegistry.Register("DyeColorAsInt64", new ConventionPack { new DyeColorMemberConvention() }, _ => true);
                s_conventionRegistered = true;
            }
            foreach (var type in StoredTypes)
            {
                foreach (var member in DyeMembers(BsonClassMap.LookupClassMap(type)))
                {
                    if (!IsInt64(member))
                        throw new InvalidOperationException(
                            $"{type.Name}.{member.MemberName} was mapped to BSON before DyeColorBson.Register ran, so it keeps " +
                            "the checked Int32 uint serializer and every dye colour (>= 2^31) would fail to save. " +
                            "Call DyeColorBson.Register first in Program.Main.");
                }
            }
            s_verified = true;
        }
    }

 /// <summary>Members of <paramref name="map"/> that the convention applies to.</summary>
    internal static IEnumerable<BsonMemberMap> DyeMembers(BsonClassMap map) =>
        map.AllMemberMaps.Where(m => m.MemberType == typeof(uint) && m.MemberName.Contains(DyeMemberName, StringComparison.Ordinal));

 /// <summary>Whether <paramref name="member"/> is stored as an Int64.</summary>
    internal static bool IsInt64(BsonMemberMap member) => member.GetSerializer() is UInt32Serializer { Representation: BsonType.Int64 };

    private sealed class DyeColorMemberConvention : ConventionBase, IMemberMapConvention
    {
        public void Apply(BsonMemberMap memberMap)
        {
            if (memberMap.MemberType == typeof(uint) && memberMap.MemberName.Contains(DyeMemberName, StringComparison.Ordinal))
                memberMap.SetSerializer(Int64Colour);
        }
    }
}

/// <summary>
/// Dye colour palettes: loads the client colour picker bitmaps from <c>data/dye/ColorPicker.json</c> and resolves the
/// colour for a colour pick made in a dye request.
/// </summary>
/// <remarks>
/// <para>
/// Each dye template names a colour picker bitmap. A single-colour bitmap yields its one colour at any point. A palette
/// bitmap yields the pixel at the requested point, with y counted from the top.
/// </para>
/// <para>
/// A colour is stored as <c>0xAARRGGBB</c> with alpha 0xFF. The colour pick arrives as
/// <c>y | x &lt;&lt; 8 | bitmapNo &lt;&lt; 16</c>.
/// </para>
/// <para>
/// The file lives under <c>data/dye/</c> because the model generator reads only <c>data/*.json</c>. If the file is missing
/// or unreadable, <see cref="Shipped"/> is null and every dye is refused rather than given a guessed colour.
/// </para>
/// </remarks>
public sealed class DyeColorCatalog
{
 /// <summary>The shipped file, relative to the server's working directory like every data table.</summary>
    public static readonly string ShippedPath = Path.Combine("data", "dye", "ColorPicker.json");

 /// <summary>Text that a palette path carries in place of the bitmap number.</summary>
    private const string BitmapNumberToken = "%d";

 /// <summary>Bytes per palette pixel: the bitmap stores them in B, G, R order.</summary>
    private const int BytesPerPixel = 3;

 /// <summary>Alpha of every dye colour, in the top byte of <c>0xAARRGGBB</c>.</summary>
    private const uint OpaqueAlpha = 0xFF000000u;

 /// <summary>Bit position of the red channel in <c>0xAARRGGBB</c>.</summary>
    private const int RedShift = 16;

 /// <summary>Bit position of the green channel in <c>0xAARRGGBB</c>.</summary>
    private const int GreenShift = 8;

 /// <summary>Mask of one byte field of the colour pick.</summary>
    private const uint PickFieldMask = 0xFF;

 /// <summary>Bit position of the x coordinate in the colour pick: <c>y | x &lt;&lt; 8 | bitmapNo &lt;&lt; 16</c>.</summary>
    private const int PickXShift = 8;

 /// <summary>Bit position of the palette bitmap number in the colour pick.</summary>
    private const int PickBitmapShift = 16;

    private readonly int _width;
    private readonly int _height;
    private readonly Dictionary<string, uint> _solid;
    private readonly Dictionary<string, byte[]> _palettes;

    private DyeColorCatalog(int width, int height, Dictionary<string, uint> solid, Dictionary<string, byte[]> palettes)
    {
        _width = width;
        _height = height;
        _solid = solid;
        _palettes = palettes;
    }

    private static readonly Lazy<DyeColorCatalog?> s_shipped = new(() => TryLoad(ShippedPath));

 /// <summary>The shipped catalog, or null when the file is missing or unreadable.</summary>
    public static DyeColorCatalog? Shipped => s_shipped.Value;

 /// <summary><see cref="Load"/>, or null on any failure.</summary>
    public static DyeColorCatalog? TryLoad(string path)
    {
        try { return File.Exists(path) ? Load(path) : null; }
        catch { return null; }
    }

 /// <summary>Reads a catalog file. Throws on a malformed file, such as a palette whose size is not width x height x 3.</summary>
    public static DyeColorCatalog Load(string path)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllBytes(path))
                   ?? throw new InvalidDataException(path + " is empty");
        if (file.Width <= 0 || file.Height <= 0)
            throw new InvalidDataException($"{path}: bitmap size {file.Width}x{file.Height}");
        var palettes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, base64) in file.Palettes)
        {
            var pixels = Convert.FromBase64String(base64);
            if (pixels.Length != file.Width * file.Height * BytesPerPixel)
                throw new InvalidDataException($"{path}: palette {key} holds {pixels.Length} bytes, not {file.Width}x{file.Height}x3");
            palettes[key] = pixels;
        }
        return new DyeColorCatalog(file.Width, file.Height,
            new Dictionary<string, uint>(file.Solid, StringComparer.OrdinalIgnoreCase), palettes);
    }

 /// <summary>
 /// The colour of bitmap <paramref name="bitmapPath"/> at (<paramref name="x"/>, <paramref name="y"/>), with y counted from
 /// the top. Returns the single colour of a single-colour bitmap at any point, the pixel of a palette, or null for an
 /// unknown bitmap or a point outside it.
 /// </summary>
    public uint? ColourAt(string bitmapPath, int x, int y)
    {
        if (_solid.TryGetValue(bitmapPath, out var colour)) return colour;
        if (!_palettes.TryGetValue(bitmapPath, out var pixels)) return null;
        if (x < 0 || y < 0 || x >= _width || y >= _height) return null;
        int i = BytesPerPixel * (y * _width + x);
        return OpaqueAlpha | ((uint)pixels[i + 2] << RedShift) | ((uint)pixels[i + 1] << GreenShift) | pixels[i];
    }

 /// <summary>
 /// The colour a dye of template <paramref name="dyeTemplate"/> gives for the colour pick <paramref name="pick"/>, or null
 /// when the template has no colour picker entry, the palette number is out of range, or the bitmap or point is unknown.
 /// </summary>
    public uint? Resolve(GameDataStore store, uint dyeTemplate, uint pick)
    {
        var row = store.ColorPickerInfo.FirstOrDefault(r => r.Group_ItemID == dyeTemplate);
        if (row is null || string.IsNullOrEmpty(row.ColorPicker_Name)) return null;
        int y = (int)(pick & PickFieldMask);
        int x = (int)((pick >> PickXShift) & PickFieldMask);
        string path = row.ColorPicker_Name;
        if (path.Contains(BitmapNumberToken, StringComparison.Ordinal))
        {
            int bitmapNo = (int)((pick >> PickBitmapShift) & PickFieldMask);
            if (bitmapNo < 1 || bitmapNo > row.Select_Num) return null;
            path = path.Replace(BitmapNumberToken, bitmapNo.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        return ColourAt(path.Replace('\\', '/'), x, y);
    }

    private sealed class CatalogFile
    {
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("solid")] public Dictionary<string, uint> Solid { get; set; } = new();
        [JsonPropertyName("palettes")] public Dictionary<string, string> Palettes { get; set; } = new();
    }
}
