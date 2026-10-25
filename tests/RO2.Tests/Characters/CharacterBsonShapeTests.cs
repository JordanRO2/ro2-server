using System.Linq;
using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Characters;

/// <summary>
/// Pins the persisted BSON shape of the <see cref="Character"/> document across the Character context carve.
/// The vestigial EF navigation properties (Account/Skills/Quests) are dropped in this slice; these tests prove
/// they leave the serialized shape and that legacy documents that still carry them keep loading
/// (the MongoDbContext registers IgnoreExtraElementsConvention).
/// </summary>
public class CharacterBsonShapeTests
{
    static CharacterBsonShapeTests() =>
        RuntimeHelpers.RunClassConstructor(typeof(MongoDbContext).TypeHandle); // register the global convention pack

    [Fact]
    public void Character_Bson_OmitsNavigationElements_AndKeepsPersistedFields()
    {
        var names = new Character { Id = 42, Name = "T", AccountId = 7 }.ToBsonDocument().Names.ToHashSet();
        Assert.DoesNotContain("Account", names);
        Assert.DoesNotContain("Skills", names);
        Assert.DoesNotContain("Quests", names);
        foreach (var n in new[] { "_id", "AccountId", "Name", "Gold", "BankGold", "Level", "AppearanceJson", "BankSlots", "AllowAnyoneReview" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void Character_Bson_WithLegacyNavigationElements_StillDeserializes()
    {
        var doc = new Character { Id = 42, Name = "T" }.ToBsonDocument();
        doc["Account"] = BsonNull.Value;
        doc["Skills"] = new BsonArray();
        doc["Quests"] = new BsonArray();
        var c = BsonSerializer.Deserialize<Character>(doc);
        Assert.Equal("T", c.Name);
        Assert.Equal(42u, c.Id);
    }
}
