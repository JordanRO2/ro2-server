using System.Text.Json.Serialization;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using Xunit;

namespace RO2.Tests.GameData;

/// <summary>
/// The typed-CSV reader (RO2.Domain.GameData.TypedCsv) that GameDataStore prefers over a table's JSON:
/// quoting, empty cells, conversion to the PROPERTY's type, header tolerance, and the JSON-name mapping.
/// </summary>
public class TypedCsvTests
{
    private sealed record Row
    {
        public uint Id { get; init; }
        public byte Small { get; init; }
        public short Signed { get; init; }
        public float Ratio { get; init; }
        public double Wide { get; init; }
        public long Big { get; init; }
        public bool Flag { get; init; }
        public string Name { get; init; } = "";
        [JsonPropertyName("3_GRADE")] public ushort Grade3 { get; init; }
    }

    private const string Header = "##META\tmagic=RO2!\ttimestamp=\n#DWORD,STRING\n";

    [Fact]
    public void QuotedFields_KeepCommasQuotesAndLineBreaks()
    {
        var rows = TypedCsv.Parse<Row>(Header + "Id,Name\n1,\"a, \"\"b\"\"\"\n2,\"line1\r\nline2\"\n3,plain\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal("a, \"b\"", rows[0].Name);
        Assert.Equal("line1\r\nline2", rows[1].Name);
        Assert.Equal("plain", rows[2].Name);
        Assert.Equal(new uint[] { 1, 2, 3 }, rows.Select(r => r.Id));
    }

    [Fact]
    public void EmptyCells_AreZeroAndEmptyString_AndShortRowsKeepDefaults()
    {
        var rows = TypedCsv.Parse<Row>(Header + "Id,Small,Ratio,Name,Flag\n7,,,,\n8\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(7u, rows[0].Id);
        Assert.Equal(0, rows[0].Small);
        Assert.Equal(0f, rows[0].Ratio);
        Assert.Equal("", rows[0].Name);
        Assert.False(rows[0].Flag);
        Assert.Equal(8u, rows[1].Id);
    }

    [Fact]
    public void Values_AreConvertedToThePropertyType_NotTheTypeRow()
    {
        // The type row says STRING for every column; the property types decide.
        var rows = TypedCsv.Parse<Row>(
            "##META\tmagic=RO2!\ttimestamp=\n#STRING,STRING,STRING,STRING,STRING,STRING,STRING\n" +
            "Id,Small,Signed,Ratio,Wide,Big,Flag\n4000000000,255,-12,0.25,1e-3,9000000000,1\n");

        var r = Assert.Single(rows);
        Assert.Equal(4000000000u, r.Id);
        Assert.Equal((byte)255, r.Small);
        Assert.Equal((short)-12, r.Signed);
        Assert.Equal(0.25f, r.Ratio);
        Assert.Equal(0.001, r.Wide);
        Assert.Equal(9000000000L, r.Big);
        Assert.True(r.Flag);
    }

    [Fact]
    public void OutOfRangeOrNonNumeric_FailsLoudlyWithRowAndColumn()
    {
        var overflow = Assert.Throws<FormatException>(() => TypedCsv.Parse<Row>(Header + "Id,Small\n1,256\n", "t.csv"));
        Assert.Contains("t.csv", overflow.Message);
        Assert.Contains("'Small'", overflow.Message);
        Assert.Throws<FormatException>(() => TypedCsv.Parse<Row>(Header + "Id\nabc\n"));
    }

    [Fact]
    public void BomCaseUnknownAndMissingColumns_AreTolerated()
    {
        var rows = TypedCsv.Parse<Row>("﻿" + Header + "NAME,Nope,id\nx,ignored,5\n");

        var r = Assert.Single(rows);
        Assert.Equal(5u, r.Id);
        Assert.Equal("x", r.Name);
        Assert.Equal((byte)0, r.Small);
    }

    [Fact]
    public void ColumnMapsToTheJsonPropertyName_NotTheCSharpName()
    {
        var rows = TypedCsv.Parse<Row>(Header + "Id,3_GRADE\n1,42\n");

        Assert.Equal((ushort)42, Assert.Single(rows).Grade3);
    }

    [Fact]
    public void HeaderOnlyOrEmptyText_GivesNoRows()
    {
        Assert.Empty(TypedCsv.Parse<Row>(Header + "Id,Name\n"));
        Assert.Empty(TypedCsv.Parse<Row>(""));
    }

    [Fact]
    public async Task GameDataStore_PrefersTheCsvOverTheJson_AndFallsBackToJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-typedcsv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Merchant.json"), "[{\"ID\":1,\"Item\":10}]");
            File.WriteAllText(Path.Combine(dir, "Merchant.csv"), Header + "ID,NPC_Name,Item\n1,0,20\n1,\"Kafra, text\",21\n");
            File.WriteAllText(Path.Combine(dir, "Teleport.json"), "[{\"Index\":5}]");

            var store = new GameDataStore();
            await store.LoadAsync(dir);

            Assert.Equal(new uint[] { 20, 21 }, store.GetMerchantItems(1)); // CSV won
            Assert.True(store.Teleports.ContainsKey(5));                    // JSON fallback
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ShippedRefineTables_LoadFromCsvOnly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "REFINE_RATE.csv")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var data = Path.Combine(dir.FullName, "data");
        Assert.False(File.Exists(Path.Combine(data, "REFINE_RATE.json")));
        Assert.False(File.Exists(Path.Combine(data, "REFINE_ITEM.json")));

        var store = new GameDataStore();
        await store.LoadAsync(data);

        Assert.Equal(20, store.REFINERATE.Count);
        Assert.Equal(36, store.REFINEITEM.Count);
        Assert.Contains(store.REFINEITEM, r => r._3_GRADE_RATE != 0 || r._2_GRADE_RATE != 0);
    }

    [Fact]
    public async Task GameDataStore_CustomDirOverlaysTheDataDir_CsvThenJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "ro2-custom-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var custom = Path.Combine(root, "custom", "data");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(custom);
        try
        {
            // Merchant: custom CSV beats custom JSON, data CSV and data JSON.
            File.WriteAllText(Path.Combine(data, "Merchant.json"), "[{\"ID\":1,\"Item\":10}]");
            File.WriteAllText(Path.Combine(data, "Merchant.csv"), Header + "ID,NPC_Name,Item\n1,0,11\n");
            File.WriteAllText(Path.Combine(custom, "Merchant.json"), "[{\"ID\":1,\"Item\":12}]");
            File.WriteAllText(Path.Combine(custom, "Merchant.csv"), Header + "ID,NPC_Name,Item\n1,0,13\n");
            // Teleport: custom JSON beats data CSV.
            File.WriteAllText(Path.Combine(data, "Teleport.csv"), Header + "Index\n5\n");
            File.WriteAllText(Path.Combine(custom, "Teleport.json"), "[{\"Index\":6}]");
            // DropTable: retail is header-only, custom supplies the rows.
            const string dropHeader = Header + "Drop_ID,Slot,Item_ID,Min_Count,Max_Count,Drop_Rate,Broadcast,Comment\n";
            File.WriteAllText(Path.Combine(data, "DropTable.csv"), dropHeader);
            File.WriteAllText(Path.Combine(custom, "DropTable.csv"), dropHeader + "7,1,100,1,2,10000,0,\"custom, pool\"\n");

            var store = new GameDataStore();
            await store.LoadAsync(data);

            Assert.Equal(new uint[] { 13 }, store.GetMerchantItems(1));
            Assert.True(store.Teleports.ContainsKey(6));
            Assert.False(store.Teleports.ContainsKey(5));
            var drop = Assert.Single(store.GetDropTable(7));
            Assert.Equal(100u, drop.Item_ID);
            Assert.Equal("custom, pool", drop.Comment);
            Assert.Equal(100u, Assert.Single(store.RollDrops(7)).ItemId); // Drop_Rate 10000 always rolls
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ShippedDropTable_RetailSlotIsFormatOnly_CustomOverlayCarriesTheEstimateSeed()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "DropTable.csv")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var data = Path.Combine(dir.FullName, "data");
        Assert.False(File.Exists(Path.Combine(data, "DropTable.json")));
        Assert.Empty(TypedCsv.Load<DropTableEntry>(Path.Combine(data, "DropTable.csv"))); // retail: header only, no invented data

        var store = new GameDataStore();
        await store.LoadAsync(data);

        // Drops are custom content now: the retail slot stays empty and the custom overlay (custom/data/DropTable.csv,
        // the seed data) supplies every pool. The rows are not an inert sample: each has a
        // live drop rate (1..10000) and a provenance comment, and a seeded pool rolls.
        var rows = store.DropTables.SelectMany(t => t.Value).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.InRange(r.Drop_Rate, 1, 10000));
        Assert.All(rows, r => Assert.Contains("estimated", r.Comment, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(store.RollDrops(12230000)); // an id no pool covers: no rows, no drop
    }

    [Fact]
    public void RefineItem_ThreeGradeColumns_ReachTheUnderscorePrefixedProperties()
    {
        // The generated REFINEITEM names them _3_GRADE_RATE / _2_GRADE_RATE (a C# name cannot start with a digit)
        // and carries the real column in [JsonPropertyName]; the CSV header uses the real column.
        var rows = TypedCsv.Parse<REFINEITEM>(Header + "ITEM_ID,3_GRADE_RATE,2_GRADE_RATE\n9,111,222\n");

        var r = Assert.Single(rows);
        Assert.Equal((ushort)111, r._3_GRADE_RATE);
        Assert.Equal((ushort)222, r._2_GRADE_RATE);
    }
}
