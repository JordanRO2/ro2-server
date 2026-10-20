using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The cash mall product catalog. It is loaded from <c>data/mall/GameMallCatalog2866.json</c>, served as the catalog list body (2866), and used to price purchases (1598).
/// </summary>
/// <remarks>
/// <para>
/// Each product is a fixed 43-byte record. Fields the server does not read are carried by offset so the list body is sent unchanged.
/// </para>
/// <para>
/// The price of a product is its sale price when that is above 0, otherwise its regular price. A purchase total must equal the unit price times the quantity.
/// </para>
/// <para>
/// <see cref="Build2866"/> rebuilds the list body from the loaded products. All item ids refer to rows of <c>ItemInfo.json</c> and <c>ItemCashInfo.json</c>.
/// </para>
/// </remarks>
public sealed class CashMallCatalog
{
 /// <summary>Path of the shipped catalog file, relative to the server's working directory like every data table.</summary>
    public static readonly string ShippedPath = Path.Combine("data", "mall", "GameMallCatalog2866.json");

 /// <summary>One catalog product, mirroring one 43-byte record of the list body (field meanings in the class remarks).</summary>
    public sealed record Product(uint ItemId, uint ProductId, byte Category, byte At9, byte At10, float Price,
        byte At15, byte At16, byte At17, ushort At18, byte At20, float SalePrice, byte At25,
        ulong SaleBeginUnix, ulong SaleEndUnix, byte At42)
    {
 /// <summary>The unit price: the sale price when it is above 0, otherwise the regular price.</summary>
        public long UnitPrice => SalePrice > 0 ? (long)SalePrice : (long)Price;

 /// <summary>Returns true while <paramref name="nowUnix"/> falls inside the product's sale window.</summary>
        public bool OnSaleAt(long nowUnix) => nowUnix >= 0 && (ulong)nowUnix >= SaleBeginUnix && (ulong)nowUnix <= SaleEndUnix;
    }

 /// <summary>Size in bytes of one catalog record.</summary>
    public const int RecordBytes = 43;

    private readonly Dictionary<uint, Product> _byItem;
    private readonly byte[] _body2866;

    public ushort Result { get; }
    public IReadOnlyList<Product> Products { get; }

    private CashMallCatalog(ushort result, IReadOnlyList<Product> products)
    {
        Result = result;
        Products = products;
        _byItem = new Dictionary<uint, Product>(products.Count);
        foreach (var p in products)
            if (!_byItem.TryAdd(p.ItemId, p))
                throw new InvalidDataException($"cash mall catalog lists item {p.ItemId} twice");
        _body2866 = Encode(result, products);
    }

 /// <summary>
 /// Returns the product that sells <paramref name="itemId"/>, or null when the item is not in the catalog.
 /// </summary>
    public Product? FindByItem(uint itemId) => _byItem.GetValueOrDefault(itemId);

 /// <summary>Builds the catalog list body: result code, body length, product count, then the records.</summary>
    public byte[] Build2866() => (byte[])_body2866.Clone();

    private static readonly Lazy<CashMallCatalog?> s_shipped = new(() => TryLoad(ShippedPath));

 /// <summary>
 /// The shipped catalog, or null when the file is missing or unreadable. In that case the product list is empty and every purchase names an unknown product.
 /// </summary>
    public static CashMallCatalog? Shipped => s_shipped.Value;

 /// <summary>Loads the catalog, or returns null on any failure.</summary>
    public static CashMallCatalog? TryLoad(string path)
    {
        try { return File.Exists(path) ? Load(path) : null; }
        catch { return null; }
    }

 /// <summary>Reads a catalog file. Throws on a malformed file or a price that is not a whole number.</summary>
    public static CashMallCatalog Load(string path)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllBytes(path))
                   ?? throw new InvalidDataException(path + " is empty");
        var products = new List<Product>(file.Products.Count);
        foreach (var r in file.Products)
        {
            if (r.Price != Math.Floor(r.Price) || r.SalePrice != Math.Floor(r.SalePrice) || r.Price < 0 || r.SalePrice < 0)
                throw new InvalidDataException($"cash mall product {r.ProductId}: price {r.Price} / sale {r.SalePrice} is not a whole amount");
            products.Add(new Product(r.ItemId, r.ProductId, r.Category, r.At9, r.At10, (float)r.Price, r.At15, r.At16,
                r.At17, r.At18, r.At20, (float)r.SalePrice, r.At25, r.SaleBeginUnix, r.SaleEndUnix, r.At42));
        }
        return new CashMallCatalog(file.Result, products);
    }

    private static byte[] Encode(ushort result, IReadOnlyList<Product> products)
    {
        int blobLen = 2 + products.Count * RecordBytes;
        var body = new byte[2 + 4 + blobLen];
        var span = body.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, result);
        BinaryPrimitives.WriteUInt32LittleEndian(span[2..], (uint)blobLen);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)products.Count);
        int pos = 8;
        foreach (var p in products)
        {
            var r = span.Slice(pos, RecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, p.ItemId);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], p.ProductId);
            r[8] = p.Category;
            r[9] = p.At9;
            r[10] = p.At10;
            BinaryPrimitives.WriteSingleLittleEndian(r[11..], p.Price);
            r[15] = p.At15;
            r[16] = p.At16;
            r[17] = p.At17;
            BinaryPrimitives.WriteUInt16LittleEndian(r[18..], p.At18);
            r[20] = p.At20;
            BinaryPrimitives.WriteSingleLittleEndian(r[21..], p.SalePrice);
            r[25] = p.At25;
            BinaryPrimitives.WriteUInt64LittleEndian(r[26..], p.SaleBeginUnix);
            BinaryPrimitives.WriteUInt64LittleEndian(r[34..], p.SaleEndUnix);
            r[42] = p.At42;
            pos += RecordBytes;
        }
        return body;
    }

    private sealed class CatalogFile
    {
        [JsonPropertyName("result")] public ushort Result { get; set; }
        [JsonPropertyName("products")] public List<ProductRow> Products { get; set; } = new();
    }

    private sealed class ProductRow
    {
        [JsonPropertyName("itemId")] public uint ItemId { get; set; }
        [JsonPropertyName("productId")] public uint ProductId { get; set; }
        [JsonPropertyName("category")] public byte Category { get; set; }
        [JsonPropertyName("at9")] public byte At9 { get; set; }
        [JsonPropertyName("at10")] public byte At10 { get; set; }
        [JsonPropertyName("price")] public double Price { get; set; }
        [JsonPropertyName("at15")] public byte At15 { get; set; }
        [JsonPropertyName("at16")] public byte At16 { get; set; }
        [JsonPropertyName("at17")] public byte At17 { get; set; }
        [JsonPropertyName("at18")] public ushort At18 { get; set; }
        [JsonPropertyName("at20")] public byte At20 { get; set; }
        [JsonPropertyName("salePrice")] public double SalePrice { get; set; }
        [JsonPropertyName("at25")] public byte At25 { get; set; }
        [JsonPropertyName("saleBeginUnix")] public ulong SaleBeginUnix { get; set; }
        [JsonPropertyName("saleEndUnix")] public ulong SaleEndUnix { get; set; }
        [JsonPropertyName("at42")] public byte At42 { get; set; }
    }
}
