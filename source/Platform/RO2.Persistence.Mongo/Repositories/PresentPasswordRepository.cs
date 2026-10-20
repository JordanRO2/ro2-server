using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IPresentPasswordRepository"/>, holding one hash per account in the
/// <c>present_passwords</c> collection. The password is a 4-digit code; only a salted SHA-256 hash is stored.
/// </summary>
public sealed class PresentPasswordRepository : IPresentPasswordRepository
{
 /// <summary>A gift password is 4 decimal digits.</summary>
    public const int PasswordLength = 4;

 /// <summary>True when <paramref name="password"/> is a well-formed gift password (exactly four digits).</summary>
    public static bool IsValidPassword(string? password) =>
        password is { Length: PasswordLength } && password.All(c => c is >= '0' and <= '9');

 /// <summary>One account's gift password. <c>Id</c> maps to Mongo <c>_id</c> = the account id.</summary>
    private sealed class PresentPasswordDocument
    {
        public uint Id { get; set; }
        public string Hash { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    private readonly IMongoCollection<PresentPasswordDocument> _docs;

    public PresentPasswordRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<PresentPasswordDocument>("present_passwords");
    }

    public async Task<bool> HasPasswordAsync(uint accountId, CancellationToken ct = default) =>
        await _docs.Find(d => d.Id == accountId).AnyAsync(ct);

    public async Task<bool> VerifyAsync(uint accountId, string password, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.Id == accountId).FirstOrDefaultAsync(ct);
        return doc is not null && FixedEquals(doc.Hash, HashOf(accountId, password));
    }

    public async Task<bool> SetAsync(uint accountId, string oldPassword, string newPassword, CancellationToken ct = default)
    {
        if (!IsValidPassword(newPassword)) return false;
        var doc = await _docs.Find(d => d.Id == accountId).FirstOrDefaultAsync(ct);
        if (doc is not null && !FixedEquals(doc.Hash, HashOf(accountId, oldPassword))) return false;

        var next = new PresentPasswordDocument { Id = accountId, Hash = HashOf(accountId, newPassword), UpdatedAt = DateTime.UtcNow };
        await _docs.ReplaceOneAsync(d => d.Id == accountId, next, new ReplaceOptions { IsUpsert = true }, ct);
        return true;
    }

    private static string HashOf(uint accountId, string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{accountId}:{password}")));

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
