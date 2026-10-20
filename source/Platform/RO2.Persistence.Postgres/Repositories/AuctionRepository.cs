using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IAuctionRepository"/>: the <c>auction_articles</c> table (one row per
/// article) and the <c>auction_counters</c> id counter. Mirrors the Mongo provider exactly. Every field the
/// interface filters, sorts, projects or compare-and-sets has its own column; the never-queried item snapshot
/// (seller name/uid, item uid, count, refine, durability, cards, element, dyes, punchers, enchant, bound,
/// ability value) is one <c>item_snapshot</c> jsonb column serialized with System.Text.Json.
/// </summary>
/// <remarks>
/// Each Mongo FindOneAndUpdate on <c>State == Active</c> is one <c>UPDATE … WHERE &lt;predicate&gt; RETURNING</c>:
/// a returned row is the document after the change, no row is a lost race (null). Postgres has no unsigned
/// integers, so ulong/uint map to <c>bigint</c> (bound with a <c>long</c> cast, read back with a cast on
/// <c>GetInt64</c>, the same signed storage Mongo uses); the state byte is a <c>smallint</c>; every DateTime is
/// bound as UTC to <c>timestamptz</c>. Unlike Mongo, a no-bid read matches only <c>top_bidder_char_id = 0</c>:
/// the "field is absent" branch only served legacy documents, and a Postgres column is never absent.
/// </remarks>
public sealed class AuctionRepository : IAuctionRepository
{
    private const string CounterId = "auctionId";

    private const short Active = (short)AuctionArticleState.Active;
    private const short Sold = (short)AuctionArticleState.Sold;

    private const string Columns =
        "id, uid, seller_char_id, state, item_id, start_price, buyout_price, top_bid, top_bidder_char_id, " +
        "top_bidder_name, top_bidder_char_uid, started_at, expires_at, buyer_char_id, closed_at, item_snapshot";

    private readonly PostgresDbContext _db;

    public AuctionRepository(PostgresDbContext db) => _db = db;

 /// <summary>The never-filtered item fields, stored in the <c>item_snapshot</c> jsonb column.</summary>
    private sealed record ItemSnapshot(
        string SellerName, ulong SellerCharUid, ulong ItemUid, int Count, byte Refine, short Durability,
        string? CardsJson, byte Element, uint DyeColor, string? SocketPunchersJson, uint DyeColor2, uint DyeColor3,
        uint FxEnchantIndex, ushort EnchantReinforceId, ushort EnchantSeconds, bool Bound, uint? AbilityValue);

    private static DateTime Utc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };

    private static void Add(NpgsqlCommand cmd, string name, long value) => cmd.Parameters.AddWithValue(name, value);

    private static void Add(NpgsqlCommand cmd, string name, DateTime value) =>
        cmd.Parameters.AddWithValue(name, NpgsqlDbType.TimestampTz, Utc(value));

    private static AuctionArticle ReadArticle(NpgsqlDataReader r)
    {
        var s = JsonSerializer.Deserialize<ItemSnapshot>(r.GetString(15))!;
        return new AuctionArticle
        {
            Id = (ulong)r.GetInt64(0),
            Uid = (ulong)r.GetInt64(1),
            SellerCharId = (uint)r.GetInt64(2),
            State = (AuctionArticleState)(byte)r.GetInt16(3),
            ItemId = (uint)r.GetInt64(4),
            StartPrice = r.GetInt64(5),
            BuyoutPrice = r.GetInt64(6),
            TopBid = r.GetInt64(7),
            TopBidderCharId = (uint)r.GetInt64(8),
            TopBidderName = r.GetString(9),
            TopBidderCharUid = (ulong)r.GetInt64(10),
            StartedAt = r.GetDateTime(11),
            ExpiresAt = r.GetDateTime(12),
            BuyerCharId = (uint)r.GetInt64(13),
            ClosedAt = r.IsDBNull(14) ? null : r.GetDateTime(14),
            SellerName = s.SellerName,
            SellerCharUid = s.SellerCharUid,
            ItemUid = s.ItemUid,
            Count = s.Count,
            Refine = s.Refine,
            Durability = s.Durability,
            CardsJson = s.CardsJson,
            Element = s.Element,
            DyeColor = s.DyeColor,
            SocketPunchersJson = s.SocketPunchersJson,
            DyeColor2 = s.DyeColor2,
            DyeColor3 = s.DyeColor3,
            FxEnchantIndex = s.FxEnchantIndex,
            EnchantReinforceId = s.EnchantReinforceId,
            EnchantSeconds = s.EnchantSeconds,
            Bound = s.Bound,
            AbilityValue = s.AbilityValue,
        };
    }

    private async Task<List<AuctionArticle>> QueryAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuctionArticle>();
        while (await reader.ReadAsync(ct)) list.Add(ReadArticle(reader));
        return list;
    }

    private async Task<AuctionArticle?> QuerySingleAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        var rows = await QueryAsync(sql, bind, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<int> CountAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        var n = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return (int)Math.Min(int.MaxValue, n);
    }

    public async Task<ulong> GetNextIdAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO auction_counters (id, seq) VALUES (@id, 1) " +
            "ON CONFLICT (id) DO UPDATE SET seq = auction_counters.seq + 1 RETURNING seq", conn);
        cmd.Parameters.AddWithValue("id", CounterId);
        var seq = await cmd.ExecuteScalarAsync(ct);
        return (ulong)(seq is long l ? l : 1L);
    }

    public async Task InsertAsync(AuctionArticle article, CancellationToken ct = default)
    {
        var snapshot = JsonSerializer.Serialize(new ItemSnapshot(
            article.SellerName, article.SellerCharUid, article.ItemUid, article.Count, article.Refine,
            article.Durability, article.CardsJson, article.Element, article.DyeColor, article.SocketPunchersJson,
            article.DyeColor2, article.DyeColor3, article.FxEnchantIndex, article.EnchantReinforceId,
            article.EnchantSeconds, article.Bound, article.AbilityValue));

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO auction_articles ({Columns}) VALUES " +
            "(@id, @uid, @seller, @state, @item, @start, @buyout, @tb, @tbc, @tbn, @tbu, @started, @expires, " +
            "@buyer, @closed, @snap)", conn);
        Add(cmd, "id", (long)article.Id);
        Add(cmd, "uid", (long)article.Uid);
        Add(cmd, "seller", article.SellerCharId);
        cmd.Parameters.AddWithValue("state", NpgsqlDbType.Smallint, (short)(byte)article.State);
        Add(cmd, "item", article.ItemId);
        Add(cmd, "start", article.StartPrice);
        Add(cmd, "buyout", article.BuyoutPrice);
        Add(cmd, "tb", article.TopBid);
        Add(cmd, "tbc", article.TopBidderCharId);
        cmd.Parameters.AddWithValue("tbn", article.TopBidderName ?? string.Empty);
        Add(cmd, "tbu", (long)article.TopBidderCharUid);
        Add(cmd, "started", article.StartedAt);
        Add(cmd, "expires", article.ExpiresAt);
        Add(cmd, "buyer", article.BuyerCharId);
        cmd.Parameters.Add(new NpgsqlParameter("closed", NpgsqlDbType.TimestampTz)
        {
            Value = article.ClosedAt is { } closed ? Utc(closed) : DBNull.Value,
        });
        cmd.Parameters.AddWithValue("snap", NpgsqlDbType.Jsonb, snapshot);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<AuctionArticle?> GetByUidAsync(ulong uid, CancellationToken ct = default) =>
        QuerySingleAsync($"SELECT {Columns} FROM auction_articles WHERE uid = @uid LIMIT 1",
            c => Add(c, "uid", (long)uid), ct);

    public async Task<IReadOnlyList<AuctionArticle>> GetActiveBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default) =>
        await QueryAsync(
            $"SELECT {Columns} FROM auction_articles WHERE seller_char_id = @seller AND state = {Active} " +
            "AND expires_at > @now ORDER BY started_at DESC",
            c => { Add(c, "seller", sellerCharId); Add(c, "now", nowUtc); }, ct);

    public Task<int> CountActiveBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default) =>
        CountAsync(
            $"SELECT COUNT(*) FROM auction_articles WHERE seller_char_id = @seller AND state = {Active} AND expires_at > @now",
            c => { Add(c, "seller", sellerCharId); Add(c, "now", nowUtc); }, ct);

    public async Task<IReadOnlyList<AuctionArticle>> GetActiveSortRowsAsync(IReadOnlyCollection<uint>? itemIds, DateTime nowUtc, CancellationToken ct = default)
    {
        if (itemIds is { Count: 0 }) return new List<AuctionArticle>();

        var sql = "SELECT id, item_id, start_price, buyout_price, top_bid, top_bidder_char_id, expires_at " +
                  $"FROM auction_articles WHERE state = {Active} AND expires_at > @now";
        if (itemIds != null) sql += " AND item_id = ANY(@ids)";

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        Add(cmd, "now", nowUtc);
        if (itemIds != null) cmd.Parameters.AddWithValue("ids", itemIds.Select(i => (long)i).ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuctionArticle>();
        while (await r.ReadAsync(ct))
        {
            
            list.Add(new AuctionArticle
            {
                Id = (ulong)r.GetInt64(0),
                ItemId = (uint)r.GetInt64(1),
                StartPrice = r.GetInt64(2),
                BuyoutPrice = r.GetInt64(3),
                TopBid = r.GetInt64(4),
                TopBidderCharId = (uint)r.GetInt64(5),
                ExpiresAt = r.GetDateTime(6),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetByIdsAsync(IReadOnlyCollection<ulong> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new List<AuctionArticle>();
        return await QueryAsync($"SELECT {Columns} FROM auction_articles WHERE id = ANY(@ids)",
            c => c.Parameters.AddWithValue("ids", ids.Select(i => (long)i).ToArray()), ct);
    }

    public async Task<IReadOnlyList<uint>> GetActiveItemIdsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT item_id FROM auction_articles WHERE state = {Active} AND expires_at > @now", conn);
        Add(cmd, "now", nowUtc);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<uint>();
        while (await r.ReadAsync(ct)) list.Add((uint)r.GetInt64(0));
        return list;
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetDueBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default) =>
        await QueryAsync(
            $"SELECT {Columns} FROM auction_articles WHERE seller_char_id = @seller AND state = {Active} AND expires_at <= @now",
            c => { Add(c, "seller", sellerCharId); Add(c, "now", nowUtc); }, ct);

    public async Task<IReadOnlyList<AuctionArticle>> GetDueAsync(DateTime nowUtc, int limit, CancellationToken ct = default) =>
        await QueryAsync(
            $"SELECT {Columns} FROM auction_articles WHERE state = {Active} AND expires_at <= @now " +
            "ORDER BY expires_at ASC LIMIT @limit",
            c => { Add(c, "now", nowUtc); Add(c, "limit", Math.Max(1, limit)); }, ct);

    
    public Task<AuctionArticle?> TryCancelAsync(ulong uid, uint sellerCharId, DateTime nowUtc, CancellationToken ct = default) =>
        CloseAsync("AND seller_char_id = @seller", c => Add(c, "seller", sellerCharId),
            uid, AuctionArticleState.Cancelled, 0, nowUtc, ct);

    public Task<AuctionArticle?> TrySellAsync(ulong uid, uint buyerCharId, DateTime nowUtc, CancellationToken ct = default) =>
        CloseAsync("AND expires_at > @now", _ => { }, uid, AuctionArticleState.Sold, buyerCharId, nowUtc, ct);

    public Task<AuctionArticle?> TryExpireAsync(ulong uid, DateTime nowUtc, CancellationToken ct = default) =>
        CloseAsync("AND expires_at <= @now", _ => { }, uid, AuctionArticleState.Expired, 0, nowUtc, ct);

    private Task<AuctionArticle?> CloseAsync(string extraPredicate, Action<NpgsqlCommand> bindExtra, ulong uid,
        AuctionArticleState to, uint buyerCharId, DateTime nowUtc, CancellationToken ct) =>
        QuerySingleAsync(
            "UPDATE auction_articles SET state = @to, buyer_char_id = @buyer, closed_at = @now " +
            $"WHERE uid = @uid AND state = {Active} {extraPredicate} RETURNING {Columns}",
            c =>
            {
                c.Parameters.AddWithValue("to", NpgsqlDbType.Smallint, (short)(byte)to);
                Add(c, "buyer", buyerCharId);
                Add(c, "now", nowUtc);
                Add(c, "uid", (long)uid);
                bindExtra(c);
            }, ct);

    
 /// <summary>
 /// WHERE predicate: Active, unexpired and still carrying the bid <paramref name="read"/> carried (same top
 /// bidder and top bid; a no-bid read matches a zero top bidder). Binds <c>@uid</c>, <c>@now</c> and, for a
 /// bid read, <c>@rtb</c> / <c>@rtbid</c>.
 /// </summary>
    private static string OpenWithBidOf(NpgsqlCommand c, AuctionArticle read, DateTime nowUtc)
    {
        Add(c, "uid", (long)read.Uid);
        Add(c, "now", nowUtc);
        var sql = $"uid = @uid AND state = {Active} AND expires_at > @now AND ";
        if (read.TopBidderCharId == 0) return sql + "top_bidder_char_id = 0";
        Add(c, "rtb", read.TopBidderCharId);
        Add(c, "rtbid", read.TopBid);
        return sql + "top_bidder_char_id = @rtb AND top_bid = @rtbid";
    }

    private static void BindBid(NpgsqlCommand c, uint bidderCharId, string bidderName, ulong bidderCharUid, long bid)
    {
        Add(c, "bid", bid);
        Add(c, "bidder", bidderCharId);
        c.Parameters.AddWithValue("bname", bidderName ?? string.Empty);
        Add(c, "buid", (long)bidderCharUid);
    }

    public async Task<AuctionArticle?> TryPlaceBidAsync(AuctionArticle read, uint bidderCharId, string bidderName,
        ulong bidderCharUid, long bid, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };
        var where = OpenWithBidOf(cmd, read, nowUtc);
        BindBid(cmd, bidderCharId, bidderName, bidderCharUid, bid);
        cmd.CommandText = "UPDATE auction_articles SET top_bid = @bid, top_bidder_char_id = @bidder, " +
                          $"top_bidder_name = @bname, top_bidder_char_uid = @buid WHERE {where} RETURNING {Columns}";
        return await ReadSingleAsync(cmd, ct);
    }

 /// <summary>
 /// Active -> Sold to <paramref name="bidderCharId"/> at <paramref name="bid"/>, recorded as the top bid, under
 /// the same condition as <see cref="TryPlaceBidAsync"/>, plus the article's seller being
 /// <paramref name="sellerCharId"/> when that is not 0. Null when that no longer holds.
 /// </summary>
    private async Task<AuctionArticle?> TrySellToBidderAsync(AuctionArticle read, uint sellerCharId, uint bidderCharId,
        string bidderName, ulong bidderCharUid, long bid, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };
        var where = OpenWithBidOf(cmd, read, nowUtc);
        if (sellerCharId != 0)
        {
            Add(cmd, "seller", sellerCharId);
            where += " AND seller_char_id = @seller";
        }
        BindBid(cmd, bidderCharId, bidderName, bidderCharUid, bid);
        cmd.CommandText = "UPDATE auction_articles SET top_bid = @bid, top_bidder_char_id = @bidder, " +
                          $"top_bidder_name = @bname, top_bidder_char_uid = @buid, state = {Sold}, " +
                          $"buyer_char_id = @bidder, closed_at = @now WHERE {where} RETURNING {Columns}";
        return await ReadSingleAsync(cmd, ct);
    }

    private static async Task<AuctionArticle?> ReadSingleAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadArticle(reader) : null;
    }

    public Task<AuctionArticle?> TryWinAtInstantPriceAsync(AuctionArticle read, uint bidderCharId, string bidderName,
        ulong bidderCharUid, long bid, DateTime nowUtc, CancellationToken ct = default) =>
        TrySellToBidderAsync(read, 0, bidderCharId, bidderName, bidderCharUid, bid, nowUtc, ct);

    public Task<AuctionArticle?> TryAcceptTopBidAsync(AuctionArticle read, uint sellerCharId, DateTime nowUtc, CancellationToken ct = default) =>
        TrySellToBidderAsync(read, sellerCharId, read.TopBidderCharId, read.TopBidderName, read.TopBidderCharUid,
            read.TopBid, nowUtc, ct);

    public async Task<IReadOnlyList<AuctionArticle>> GetTopBidsAsync(uint bidderCharId, DateTime nowUtc, CancellationToken ct = default) =>
        await QueryAsync(
            $"SELECT {Columns} FROM auction_articles WHERE top_bidder_char_id = @bidder AND state = {Active} " +
            "AND expires_at > @now ORDER BY started_at DESC",
            c => { Add(c, "bidder", bidderCharId); Add(c, "now", nowUtc); }, ct);

    public Task<int> CountTopBidsAsync(uint bidderCharId, DateTime nowUtc, CancellationToken ct = default) =>
        CountAsync(
            $"SELECT COUNT(*) FROM auction_articles WHERE top_bidder_char_id = @bidder AND state = {Active} AND expires_at > @now",
            c => { Add(c, "bidder", bidderCharId); Add(c, "now", nowUtc); }, ct);
}
