using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RO2.Identity.Infrastructure.Mongo;

/// <summary>
/// Persistence document for the <see cref="Domain.Account.Account"/> aggregate. It is kept separate so the domain
/// has no persistence attributes or setters. Field names match the existing account documents.
/// </summary>
/// <remarks>
/// The document id is the account id, stored as a BSON Int32. The mapping is explicit so it does not depend on
/// driver defaults. Unknown fields, such as the embedded characters array, are ignored on read.
/// </remarks>
public sealed class AccountDocument
{
 /// <summary>The account id, stored as the Mongo <c>_id</c>.</summary>
    [BsonId]
    [BsonRepresentation(BsonType.Int32)]
    public uint Id { get; set; }

 /// <summary>The login key.</summary>
    public string Username { get; set; } = string.Empty;

 /// <summary>The BCrypt password hash.</summary>
    public string PasswordHash { get; set; } = string.Empty;

 /// <summary>The PIN hash, or <see langword="null"/> when no PIN is set.</summary>
    public string? SecondPasswordHash { get; set; }

 /// <summary>The account email.</summary>
    public string? Email { get; set; }

 /// <summary>When the account was created.</summary>
    public DateTime CreatedAt { get; set; }

 /// <summary>When the account last logged in successfully, or <see langword="null"/>.</summary>
    public DateTime? LastLogin { get; set; }

 /// <summary>The banned flag.</summary>
    public bool IsBanned { get; set; }

 /// <summary>The ban expiry, or <see langword="null"/> for a permanent ban.</summary>
    public DateTime? BanExpires { get; set; }

 /// <summary>The ban reason, or <see langword="null"/>.</summary>
    public string? BanReason { get; set; }

 /// <summary>The access level.</summary>
    public byte AccessLevel { get; set; }
}
