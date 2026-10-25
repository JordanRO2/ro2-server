using RO2.Domain.Entities;
using System.Text.Json;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// P3 unit A5: 2873, 1321, 1331, 1332 and 2580/1436. Only the 2580 tests use retail frames. Every other test pins
/// the layout of the client's reader or builder (named in the test) and is not verified against retail traffic,
/// since none of those opcodes has a frame in the reference corpora.
/// </summary>
public class UnitA5P3Tests
{
    private static readonly Lazy<KharaCatalog> Catalog = new(() => new KharaCatalog(LoadRows()));

    
    [Fact]
    public void AnsGameMallItemPrice2873_IsResultZeroAndEmptyCompactVector()
    {
        
        
        Assert.Equal("00000100", Hex(GameMallItemPriceHandler.BuildAnsGameMallItemPrice2873Empty()));
    }

    [Fact]
    public void ReqGameMallItemPrice1601_ParsesCompactCountThenU32Ids()
    {
        
        var body = Convert.FromHexString("0102" + "40420f00" + "a0860100");
        Assert.Equal(new uint[] { 1000000, 100000 }, GameMallItemPriceHandler.ParseRequestedIds(body));
        
        Assert.Equal(new uint[] { 1000000 }, GameMallItemPriceHandler.ParseRequestedIds(Convert.FromHexString("010540420f00")));
        Assert.Null(GameMallItemPriceHandler.ParseRequestedIds(Convert.FromHexString("0002")));
    }

    
    [Fact]
    public void ReqPCItemDrop1321_ParsesTheBuilderLayout()
    {
        
        
        var body = Convert.FromHexString(
            "011e" + "1e00" + "a0e2" + "1114b90c" + "05" + "0700000000a2d23b" + "02"
            + "0000c842" + "00004843" + "00009643");
        Assert.True(ItemDropHandler.TryParse(body, out var r));
        Assert.Equal((ushort)58016, r.MsgTag);
        Assert.Equal(0x0CB91411u, r.ActorHandle);
        Assert.Equal((byte)5, r.Slot);
        Assert.Equal(0x3BD2A20000000007UL, r.ItemUid);
        Assert.Equal((byte)2, r.Extra);
        Assert.Equal(100f, r.X);
        Assert.Equal(200f, r.Y);
        Assert.Equal(300f, r.Z);
        
        Assert.False(ItemDropHandler.TryParse(body.AsSpan(0, body.Length - 1), out _));
    }

    
    [Fact]
    public void ReqPCItemRune1331_OnlyTheEnvelopeIsRead()
    {
        
        Assert.True(ItemRuneHandler.TryDescribe(Convert.FromHexString("01060600aabb0000"), out var len, out var tag));
        Assert.Equal(6, len);
        Assert.Equal((ushort)0xBBAA, tag);
        Assert.True(ItemRuneHandler.TryDescribe(Convert.FromHexString("0102aaaa"), out len, out tag));
        Assert.Equal(2, len);
        Assert.Null(tag);
        Assert.False(ItemRuneHandler.TryDescribe(Convert.FromHexString("00"), out _, out _));
    }

    
    [Fact]
    public void ReqPCStealItemPickUp1332_ParsesTheBuilderLayout()
    {
        
        
        var body = Convert.FromHexString(
            "0115" + "150006e4" + "1114b90c" + "0700000000a2d23b" + "00" + "13440b07");
        Assert.True(ItemEnforceHandler.TryParse(body, out var r));
        Assert.Equal((ushort)58374, r.MsgTag);
        Assert.Equal(0x0CB91411u, r.ActorHandle);
        Assert.Equal(0x3BD2A20000000007UL, r.ItemUid);
        Assert.Equal((byte)0, r.Flag);
        Assert.Equal(0x070B4413u, r.TargetHandle);
        Assert.False(ItemEnforceHandler.TryParse(body.AsSpan(0, body.Length - 1), out _));
    }

    
    [Fact]
    public void AnsKharaTimerStart2580_ReproducesRetailFrames()
    {
        
        Assert.Equal("61010000", Hex(KharaService.BuildAnsKharaTimerStart2580(353)));
        Assert.Equal("7f010000", Hex(KharaService.BuildAnsKharaTimerStart2580(383)));
    }

    [Fact]
    public void TimedKharas_AreExactlyTheFailType38Ones()
    {
        var timed = Enumerable.Range(1, 2000).Select(i => (uint)i)
            .Where(id => Catalog.Value.Get(id) is { } d && KharaService.IsTimed(d)).ToArray();
        Assert.Equal(new uint[] { 353, 380, 383, 387, 388, 389, 390 }, timed);
    }

    [Fact]
    public void Accept_TimedKhara_Sends2580AfterThe2574_UntimedDoesNot()
    {
        
        
        
        var engine = Engine(new KharaStateDocument { Id = 1 });
        Assert.True(engine.Accept(383));
        Assert.Equal(new[] { "2580:7f010000" }, Frames(engine));

        var other = Engine(new KharaStateDocument { Id = 1 });
        Assert.True(other.Accept(384));
        Assert.DoesNotContain(other.Frames, f => f.Opcode == 2580);
    }

    [Fact]
    public void ReqKharaTimerStart1436_AnswersOnlyAnAcceptedTimedKhara()
    {
        
        Assert.True(KharaTimerStartHandler.TryReadKharaId(Convert.FromHexString("6101000001000000"), out var id));
        Assert.Equal(353u, id);
        Assert.False(KharaTimerStartHandler.TryReadKharaId(Convert.FromHexString("610100"), out _));

        Assert.True(KharaTimerStartHandler.ShouldAnswer(353, new uint[] { 353, 589 }, Catalog.Value));
        Assert.False(KharaTimerStartHandler.ShouldAnswer(353, new uint[] { 589 }, Catalog.Value));   
        Assert.False(KharaTimerStartHandler.ShouldAnswer(384, new uint[] { 384 }, Catalog.Value));   
    }

    
    private static KharaEngine Engine(KharaStateDocument state) =>
        new(Catalog.Value, state, new KharaFacts { Level = 50 });

    private static string[] Frames(KharaEngine engine) =>
        engine.Frames.Select(f => $"{f.Opcode}:{Hex(f.Body)}").ToArray();

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static List<KharaInfo> LoadRows()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Khara_Info.json")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Khara_Info.json");
        return JsonSerializer.Deserialize<List<KharaInfo>>(
            File.ReadAllText(Path.Combine(dir.FullName, "data", "Khara_Info.json")), DataJsonOptions)!;
    }
}
