using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using RO2.Guilds.Infrastructure.Mongo;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Guilds.Domain;

namespace RO2.Tests.Integration;

/// <summary>
/// Pins the stored shape of a <see cref="Guild"/> against both providers without a database: the Mongo document
/// (element names, <c>_id</c>, no Gate) and the Postgres jsonb text. The goldens were captured from the aggregate
/// before it moved out of GuildService.cs, so a pass proves the move changed no stored byte.
/// </summary>
public sealed class GuildPersistenceShapeTests
{
    static GuildPersistenceShapeTests()
    {
        DyeColorBson.Register();
        GuildBsonMap.Register();
    }

    private static Guild Sample() => new()
    {
        Id = 7,
        Guid16 = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
        Name = "Knights",
        MasterCharId = 4001,
        Notice = "welcome",
        Gold = 9_000_000_000,
        CreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Exp = 1UL << 40,
        Ranks = { new GuildRank { Index = 0, Name = " 11832", Permissions = 15 }, new GuildRank { Index = 1, Name = "Officer", Permissions = 7 } },
        Members =
        {
            new GuildMember
            {
                CharId = 4001, Gid = 1UL << 41, Name = "Alice", Level = 50, Job = 3, AuthId = 1,
                ContribPoints = 42, BribePercent = 10, JoinedUtc = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            },
            new GuildMember { CharId = 5, Name = "Bob" },
        },
        Storage =
        {
            new GuildStorageItem
            {
                Uid = 1UL << 42, ItemId = 501, Count = 3, Refine = 5, Durability = 120, CardsJson = "[1,2]",
                Element = 2, DyeColor = 0xFF112233, SocketPunchersJson = "[9]", DyeColor2 = 0xFF445566, DyeColor3 = 0xFF778899,
                FxEnchantIndex = 7, EnchantReinforceId = 8, EnchantSeconds = 9, Bound = true, AbilityValue = 77,
                StorageSlot = 2, DepositedByCharId = 1234,
            },
            new GuildStorageItem { Uid = 1, ItemId = 2 },
        },
        Skills = { 101u, 202u },
    };

    private static void AssertSame(Guild a, Guild b)
    {
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Guid16, b.Guid16);
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.MasterCharId, b.MasterCharId);
        Assert.Equal(a.Notice, b.Notice);
        Assert.Equal(a.Gold, b.Gold);
        Assert.Equal(a.CreatedUtc, b.CreatedUtc);
        Assert.Equal(a.Exp, b.Exp);
        Assert.Equal(a.Skills, b.Skills);
        Assert.Equal(a.Ranks.Count, b.Ranks.Count);
        for (var i = 0; i < a.Ranks.Count; i++)
            Assert.Equal(JsonSerializer.Serialize(a.Ranks[i]), JsonSerializer.Serialize(b.Ranks[i]));
        Assert.Equal(a.Members.Count, b.Members.Count);
        for (var i = 0; i < a.Members.Count; i++)
            Assert.Equal(JsonSerializer.Serialize(a.Members[i]), JsonSerializer.Serialize(b.Members[i]));
        Assert.Equal(a.Storage.Count, b.Storage.Count);
        for (var i = 0; i < a.Storage.Count; i++)
            Assert.Equal(JsonSerializer.Serialize(a.Storage[i]), JsonSerializer.Serialize(b.Storage[i]));
    }

    [Fact]
    public void Mongo_document_shape_is_pinned_and_round_trips()
    {
        var guild = Sample();
        var doc = guild.ToBsonDocument();
        Assert.Equal(GoldenBson, doc.ToJson(new JsonWriterSettings { Indent = false, OutputMode = JsonOutputMode.CanonicalExtendedJson }));
        Assert.False(doc.Contains("Gate"));
        Assert.False(doc.Contains("Id"));

        var back = BsonSerializer.Deserialize<Guild>(doc);
        AssertSame(guild, back);
        Assert.Equal(doc, back.ToBsonDocument());
    }

    [Fact]
    public void Postgres_json_shape_is_pinned_and_round_trips()
    {
        var guild = Sample();
        var json = JsonSerializer.Serialize(guild);
        Assert.Equal(GoldenJson, json);
        Assert.DoesNotContain("Gate", json);

        var back = JsonSerializer.Deserialize<Guild>(json)!;
        AssertSame(guild, back);
        Assert.Equal(json, JsonSerializer.Serialize(back));
    }

    private const string GoldenBson = """
{ "_id" : { "$numberInt" : "7" }, "Guid16" : { "$binary" : { "base64" : "AQIDBAUGBwgJCgsMDQ4PEA==", "subType" : "00" } }, "Name" : "Knights", "MasterCharId" : { "$numberInt" : "4001" }, "Notice" : "welcome", "Gold" : { "$numberLong" : "9000000000" }, "CreatedUtc" : { "$date" : { "$numberLong" : "1767323045000" } }, "Exp" : { "$numberLong" : "1099511627776" }, "Ranks" : [{ "Index" : { "$numberInt" : "0" }, "Name" : " 11832", "Permissions" : { "$numberInt" : "15" } }, { "Index" : { "$numberInt" : "1" }, "Name" : "Officer", "Permissions" : { "$numberInt" : "7" } }], "Members" : [{ "CharId" : { "$numberInt" : "4001" }, "Gid" : { "$numberLong" : "2199023255552" }, "Name" : "Alice", "Level" : { "$numberInt" : "50" }, "Job" : { "$numberInt" : "3" }, "AuthId" : { "$numberInt" : "1" }, "ContribPoints" : { "$numberLong" : "42" }, "BribePercent" : { "$numberInt" : "10" }, "JoinedUtc" : { "$date" : { "$numberLong" : "1770091506000" } } }, { "CharId" : { "$numberInt" : "5" }, "Gid" : { "$numberLong" : "0" }, "Name" : "Bob", "Level" : { "$numberInt" : "1" }, "Job" : { "$numberInt" : "0" }, "AuthId" : { "$numberInt" : "0" }, "ContribPoints" : { "$numberLong" : "0" }, "BribePercent" : { "$numberInt" : "0" }, "JoinedUtc" : null }], "Storage" : [{ "Uid" : { "$numberLong" : "4398046511104" }, "ItemId" : { "$numberInt" : "501" }, "Count" : { "$numberInt" : "3" }, "Refine" : { "$numberInt" : "5" }, "Durability" : { "$numberInt" : "120" }, "CardsJson" : "[1,2]", "Element" : { "$numberInt" : "2" }, "DyeColor" : { "$numberLong" : "4279312947" }, "SocketPunchersJson" : "[9]", "DyeColor2" : { "$numberLong" : "4282668390" }, "DyeColor3" : { "$numberLong" : "4286023833" }, "FxEnchantIndex" : { "$numberInt" : "7" }, "EnchantReinforceId" : { "$numberInt" : "8" }, "EnchantSeconds" : { "$numberInt" : "9" }, "Bound" : true, "AbilityValue" : { "$numberInt" : "77" }, "StorageSlot" : { "$numberInt" : "2" }, "DepositedByCharId" : { "$numberInt" : "1234" } }, { "Uid" : { "$numberLong" : "1" }, "ItemId" : { "$numberInt" : "2" }, "Count" : { "$numberInt" : "1" }, "Refine" : { "$numberInt" : "0" }, "Durability" : { "$numberInt" : "-1" }, "CardsJson" : null, "Element" : { "$numberInt" : "0" }, "DyeColor" : { "$numberLong" : "0" }, "SocketPunchersJson" : null, "DyeColor2" : { "$numberLong" : "0" }, "DyeColor3" : { "$numberLong" : "0" }, "FxEnchantIndex" : { "$numberInt" : "0" }, "EnchantReinforceId" : { "$numberInt" : "0" }, "EnchantSeconds" : { "$numberInt" : "0" }, "Bound" : false, "AbilityValue" : null, "StorageSlot" : { "$numberInt" : "0" }, "DepositedByCharId" : { "$numberInt" : "0" } }], "Skills" : [{ "$numberInt" : "101" }, { "$numberInt" : "202" }] }
""";
    private const string GoldenJson = """
{"Id":7,"Guid16":"AQIDBAUGBwgJCgsMDQ4PEA==","Name":"Knights","MasterCharId":4001,"Notice":"welcome","Gold":9000000000,"CreatedUtc":"2026-01-02T03:04:05Z","Exp":1099511627776,"Ranks":[{"Index":0,"Name":" 11832","Permissions":15},{"Index":1,"Name":"Officer","Permissions":7}],"Members":[{"CharId":4001,"Gid":2199023255552,"Name":"Alice","Level":50,"Job":3,"AuthId":1,"ContribPoints":42,"BribePercent":10,"JoinedUtc":"2026-02-03T04:05:06Z"},{"CharId":5,"Gid":0,"Name":"Bob","Level":1,"Job":0,"AuthId":0,"ContribPoints":0,"BribePercent":0,"JoinedUtc":null}],"Storage":[{"Uid":4398046511104,"ItemId":501,"Count":3,"Refine":5,"Durability":120,"CardsJson":"[1,2]","Element":2,"DyeColor":4279312947,"SocketPunchersJson":"[9]","DyeColor2":4282668390,"DyeColor3":4286023833,"FxEnchantIndex":7,"EnchantReinforceId":8,"EnchantSeconds":9,"Bound":true,"AbilityValue":77,"StorageSlot":2,"DepositedByCharId":1234},{"Uid":1,"ItemId":2,"Count":1,"Refine":0,"Durability":-1,"CardsJson":null,"Element":0,"DyeColor":0,"SocketPunchersJson":null,"DyeColor2":0,"DyeColor3":0,"FxEnchantIndex":0,"EnchantReinforceId":0,"EnchantSeconds":0,"Bound":false,"AbilityValue":null,"StorageSlot":0,"DepositedByCharId":0}],"Skills":[101,202]}
""";
}
