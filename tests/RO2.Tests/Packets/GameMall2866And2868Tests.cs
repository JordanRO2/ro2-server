using System.Buffers.Binary;
using System.Security.Cryptography;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte layout of the two cash-mall answers sent during world entry: 2866 AnsGameMallItemListAll (to 1592)
/// and 2868 AnsGameMallMyInfo (to 1596).
/// </summary>
/// <remarks>
/// Each is sent only as the answer to its request, on the zone connection, right after the first 2310 and
/// before 1304.
/// </remarks>
public class GameMall2866And2868Tests
{
    
    
    private const string Retail2868ZeroHex = "0000000000000000000000000000";
    private const string Retail2868BalanceHex = "0000000070410000000000000000";

    
    
    private const string Retail2866HeaderHex = "0000071800008f00";
    private const string Retail2866Md5 = "b00d932846f600615993490e40eccecf";
    private const int Retail2866BodyLength = 6157;
    private const int CatalogRecordSize = 43;

    [Fact]
    public void ZeroBalanceAnswerIsRetailsZeroFrameByteForByte()
    {
        Assert.Equal(Convert.FromHexString(Retail2868ZeroHex), CashMallPackets.Build2868(0));
    }

    [Fact]
    public void BalanceAnswerIsRetailsFrameByteForByte()
    {
        
        Assert.Equal(Convert.FromHexString(Retail2868BalanceHex), CashMallPackets.Build2868(15));
        Assert.Equal(15.0f, BinaryPrimitives.ReadSingleLittleEndian(Convert.FromHexString(Retail2868BalanceHex).AsSpan(2)));
    }

    [Fact]
    public void RetailCatalogHeaderFollowsTheVarBlobRule()
    {
        
        
        var header = Convert.FromHexString(Retail2866HeaderHex);

        var blobLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(header));
        Assert.Equal((uint)(Retail2866BodyLength - 6), blobLength);
        Assert.Equal(143, count);
        Assert.Equal(blobLength, (uint)(2 + count * CatalogRecordSize));
    }

    [Fact]
    public void ShippedCatalogRebuildsRetailsBodyByteForByte()
    {
        var catalog = CashMallCatalog.Load(CatalogPath());
        var body = catalog.Build2866();

        Assert.Equal(Retail2866BodyLength, body.Length);
        Assert.Equal(Convert.FromHexString(Retail2866HeaderHex), body[..8]);
        Assert.Equal(Retail2866Md5, Convert.ToHexStringLower(MD5.HashData(body)));
        Assert.Equal(143, catalog.Products.Count);
    }

    [Fact]
    public void CatalogPricesTheProductsRetailSold()
    {
        
        
        
        var catalog = CashMallCatalog.Load(CatalogPath());

        Assert.Equal((3043u, 30L), Priced(catalog, 70004012));
        Assert.Equal((3333u, 200L), Priced(catalog, 60002178));
        Assert.Equal((3065u, 100L), Priced(catalog, 66600000));
        Assert.Equal((3402u, 500L), Priced(catalog, 99900079));
        Assert.Null(catalog.FindByItem(70001063)); 
    }

    [Fact]
    public void EmptyCatalogIsAWellFormedVarBlobWithNoRecords()
    {
        var body = ReqGameMallItemListAllHandler.BuildEmptyCatalog2866();

        Assert.Equal(Convert.FromHexString("0000020000000000"), body);

        
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.Equal((uint)(body.Length - 6), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)));
    }

    private static (uint ProductId, long UnitPrice) Priced(CashMallCatalog catalog, uint itemId)
    {
        var p = catalog.FindByItem(itemId);
        Assert.NotNull(p);
        return (p.ProductId, p.UnitPrice);
    }

 /// <summary>data/mall/GameMallCatalog2866.json, found by walking up from the test binaries.</summary>
    internal static string CatalogPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "mall", "GameMallCatalog2866.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/mall/GameMallCatalog2866.json");
    }
}
