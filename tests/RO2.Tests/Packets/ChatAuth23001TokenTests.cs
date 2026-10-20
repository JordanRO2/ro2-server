using RO2.Application.Interfaces;
using System.Text;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Server.Chat.Handlers.Rmi;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Packets;

/// <summary>
/// Chat identity binding: 23001 carries the 16-byte one-shot key issued by the Zone on 1516. ChatAuthHandler
/// redeems it and takes the identity from the token record, refusing any 23001 whose key is missing, forged,
/// expired or already used.
/// </summary>
/// <remarks>
/// Logic and refusal tests need no MongoDB; they inject the redeemer.
/// <see cref="ChatAuth23001TokenMongoIntegrationTests"/> covers the real one-shot round-trip and skips when
/// MongoDB is unavailable.
/// </remarks>
public sealed class ChatAuth23001TokenTests
{
    
    private const ulong VictimGid = 0x0E043DA700000999ul;
    private const ulong TokenGid = 0x0E043DA700000042ul;
    private const uint TokenAccount = 1679652u;

 /// <summary>Wire layout of the 23001 body: [u32 dataLen][u64 charUid][16-byte token][host][port].</summary>
    private static byte[] Build23001Body(ulong charUid, byte[] token16, string host = "127.0.0.1", ushort port = 7401)
    {
        using var w = new PacketWriter();
        var ip = Encoding.Unicode.GetBytes(host);
        int dataLen = 8 + 16 + 2 + ip.Length + 2;
        w.WriteUInt32((uint)dataLen);
        w.WriteUInt32((uint)charUid);
        w.WriteUInt32((uint)(charUid >> 32));
        w.WriteBytes(token16);
        w.WriteUInt16((ushort)(ip.Length / 2));
        w.WriteBytes(ip);
        w.WriteUInt16(port);
        return w.GetWrittenSpan().ToArray();
    }

    [Fact]
    public void TryReadMessengerToken_RecoversTheTokenAndClaimedUid()
    {
        var token16 = Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();
        var body = Build23001Body(VictimGid, token16);

        Assert.True(ChatAuthHandler.TryReadMessengerToken(body, out var claimed, out var hex));
        Assert.Equal(VictimGid, claimed);
        Assert.Equal(Convert.ToHexString(token16), hex);
    }

    [Fact]
    public void TryReadMessengerToken_RejectsABodyTooShortForTheToken()
    {
        
        using var w = new PacketWriter();
        w.WriteUInt32(0);
        w.WriteUInt64(VictimGid);
        Assert.False(ChatAuthHandler.TryReadMessengerToken(w.GetWrittenSpan().ToArray(), out _, out _));
    }

    [Fact]
    public async Task ValidKey_AuthsWithTheIdentityFromTheToken_NotThePacket()
    {
        var token16 = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var hex = Convert.ToHexString(token16);
        
        
        var body = Build23001Body(VictimGid, token16);
        var issued = new OneShotToken(
            hex, OneShotTokens.PurposeMessenger, TokenAccount, TokenGid, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5));

        var result = await ChatAuthHandler.AuthenticateAsync(body, h => Task.FromResult(h == hex ? issued : null));

        Assert.NotNull(result);
        Assert.Equal(TokenGid, result!.CharacterUid);   
        Assert.Equal(TokenAccount, result.AccountId);
        Assert.NotEqual(VictimGid, result.CharacterUid); 
    }

    [Fact]
    public async Task ForgedKey_IsRejected()
    {
        
        var forged = Enumerable.Range(0, 16).Select(_ => (byte)0xFF).ToArray();
        var body = Build23001Body(VictimGid, forged);

        var result = await ChatAuthHandler.AuthenticateAsync(body, _ => Task.FromResult<OneShotToken?>(null));

        Assert.Null(result);
    }

    [Fact]
    public async Task AbsentKey_ShortBody_IsRejectedWithoutCallingTheRedeemer()
    {
        using var w = new PacketWriter();
        w.WriteUInt32(0);
        w.WriteUInt64(VictimGid); 
        var calls = 0;

        var result = await ChatAuthHandler.AuthenticateAsync(
            w.GetWrittenSpan().ToArray(),
            _ => { calls++; return Task.FromResult<OneShotToken?>(null); });

        Assert.Null(result);
        Assert.Equal(0, calls); 
    }
}

/// <summary>
/// Real one-shot token round-trip against MongoDB: issues a messenger key, builds the 23001 body that carries it,
/// and redeems it through <see cref="ChatAuthHandler.AuthenticateAsync"/> with the store's <c>ConsumeAsync</c>.
/// Verifies that the identity comes from the token, that the key is single-use, and that a forged or expired
/// key fails. Passes as a no-op when MongoDB is unreachable.
/// </summary>
public sealed class ChatAuth23001TokenMongoIntegrationTests
{
    private const string ConnectionString = "mongodb://localhost:27017";
    private const ulong VictimGid = 0x0E043DA700000999ul;
    private const ulong TokenGid = 0x0E043DA700000042ul;
    private const uint TokenAccount = 1679652u;

    private readonly ITestOutputHelper _output;
    public ChatAuth23001TokenMongoIntegrationTests(ITestOutputHelper output) => _output = output;

    private bool MongoUp()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(ConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            _ = new MongoClient(settings).ListDatabaseNames(new ListDatabaseNamesOptions());
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or MongoException)
        {
            _output.WriteLine($"SKIP: Mongo unreachable at {ConnectionString} ({ex.GetType().Name}).");
            return false;
        }
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static async Task WithFreshDatabase(Func<MongoDbContext, Task> body)
    {
        var dbName = "ro2_test_chatauth_" + Guid.NewGuid().ToString("N");
        try { await body(new MongoDbContext(ConnectionString, dbName)); }
        finally { await new MongoClient(ConnectionString).DropDatabaseAsync(dbName); }
    }

    
    private static byte[] Build23001Body(ulong charUid, string tokenHex)
    {
        using var w = new PacketWriter();
        var ip = Encoding.Unicode.GetBytes("127.0.0.1");
        int dataLen = 8 + 16 + 2 + ip.Length + 2;
        w.WriteUInt32((uint)dataLen);
        w.WriteUInt32((uint)charUid);
        w.WriteUInt32((uint)(charUid >> 32));
        w.WriteBytes(Convert.FromHexString(tokenHex));
        w.WriteUInt16((ushort)(ip.Length / 2));
        w.WriteBytes(ip);
        w.WriteUInt16(7401);
        return w.GetWrittenSpan().ToArray();
    }

    private static Task<OneShotToken?> Auth(OneShotTokenStore store, byte[] body)
        => ChatAuthHandler.AuthenticateAsync(body, h => store.ConsumeAsync(h, OneShotTokens.PurposeMessenger));

    [Fact]
    public async Task ValidKey_Auths_ThenSecondUseFails_OneShot()
    {
        if (!MongoUp()) return;
        await WithFreshDatabase(async db =>
        {
            var store = new OneShotTokenStore(db);
            await store.EnsureIndexesAsync();
            var tokenHex = await store.IssueNewAsync(OneShotTokens.PurposeMessenger, TokenAccount, TokenGid, TimeSpan.FromMinutes(5));

            
            var body = Build23001Body(VictimGid, tokenHex);

            var first = await Auth(store, body);
            Assert.NotNull(first);
            Assert.Equal(TokenGid, first!.CharacterUid);
            Assert.Equal(TokenAccount, first.AccountId);

            
            Assert.Null(await Auth(store, body));
        });
    }

    [Fact]
    public async Task ForgedOrVictimClaimWithoutAKey_IsRejected()
    {
        if (!MongoUp()) return;
        await WithFreshDatabase(async db =>
        {
            var store = new OneShotTokenStore(db);
            await store.EnsureIndexesAsync();

            
            var forgedHex = OneShotTokens.NewToken();
            Assert.Null(await Auth(store, Build23001Body(VictimGid, forgedHex)));
        });
    }

    [Fact]
    public async Task ExpiredKey_IsRejected()
    {
        if (!MongoUp()) return;
        await WithFreshDatabase(async db =>
        {
            var time = new FakeTime(DateTimeOffset.UtcNow);
            var store = new OneShotTokenStore(db, time);
            var tokenHex = await store.IssueNewAsync(OneShotTokens.PurposeMessenger, TokenAccount, TokenGid, TimeSpan.FromMinutes(5));
            var body = Build23001Body(TokenGid, tokenHex);

            time.Advance(TimeSpan.FromMinutes(6)); 
            Assert.Null(await Auth(store, body));
        });
    }
}
