using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using System.Text.Json;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Khara title selection (RMI 1438 and 2582), validated through KharaTitleStore.Decide. The 2582 bodies are reference frames.
/// The instants passed to Decide are chosen so that each frame's own timestamp field matches.
/// The tests pin the rule and the byte layout.
/// </summary>
public class KharaTitleSelectReauditTests
{
    private static readonly DateTime T0 = new(2026, 7, 20, 13, 20, 36, DateTimeKind.Utc);

    private static bool Any(uint _) => true;

    private static byte[] Answer(KharaTitleState state, uint a, uint b, DateTime now, out KharaTitleDecision d, Func<uint, bool>? mayWear = null)
    {
        d = KharaTitleStore.Decide(state, a, b, now, mayWear ?? Any);
        return KharaTitleSelectPackets.Build2582(a, b, d);
    }

    
    [Fact]
    public void NewTitleA_StartsTimer_ArchiveD3e8Success()
    {
        
        
        var before = new KharaTitleState(0, 0x26B, null, null);
        var body = Answer(before, 0x244, 0x26B, T0, out var d);
        Assert.Equal(Hex("44020000c0d401006b02000000000000"), body);
        Assert.True(d.Accepted);
        Assert.Equal(new KharaTitleState(0x244, 0x26B, T0, T0), d.NewState);
    }

    [Fact]
    public void BChangeWithinTenSeconds_Refused_ArchiveD3e8()
    {
        
        
        var after = new KharaTitleState(0x244, 0x26B, T0, T0);
        var body = Answer(after, 0x244, 0x268, T0.AddMilliseconds(6547), out var d);
        Assert.Equal(Hex("440200007d0d000068020000f7540000"), body);
        Assert.False(d.Accepted);
        Assert.Equal(after, d.NewState);
    }

    [Fact]
    public void RefusalsCountDown_FromTheLastSuccess_Live2b70()
    {
        
        
        var state = KharaTitleStore.Decide(new KharaTitleState(0x244, 0, null, null), 0x244, 0x229, T0, Any).NewState;
        (int ms, string frame)[] refusals =
        {
            (2531, "440200002d1d000068020000f7540000"),
            (2672, "44020000a01c000068020000f7540000"),
            (4656, "44020000e014000068020000f7540000"),
            (6594, "440200004e0d000068020000f7540000"),
        };
        foreach (var (ms, frame) in refusals)
        {
            var body = Answer(state, 0x244, 0x268, T0.AddMilliseconds(ms), out var d);
            Assert.Equal(Hex(frame), body);
            state = d.NewState;
        }
    }

    [Fact]
    public void SettingBFromZero_IsFree_AndReSelectKeepsTheTimer_Live936b()
    {
        
        
        var first = Answer(default, 0x250, 0, T0, out var d1);
        Assert.Equal(Hex("50020000c0d401000000000000000000"), first);
        var second = Answer(d1.NewState, 0x250, 0x22C, T0.AddMilliseconds(1672), out var d2);
        Assert.Equal(Hex("5002000038ce01002c02000000000000"), second);
        Assert.True(d2.Accepted);
        Assert.Equal(T0, d2.NewState.ATimerStartUtc);
    }

    [Fact]
    public void RefusalThenSuccessAfterTenSeconds_Live3364()
    {
        
        
        
        var start = KharaTitleStore.Decide(new KharaTitleState(0x244, 0x268, null, null), 0x23C, 0x268, T0, Any);
        Assert.Equal(KharaTitleStore.TitleTimerMs, start.Timer);
        Assert.Equal(Hex("3c020000951600006b020000f7540000"), Answer(start.NewState, 0x23C, 0x26B, T0.AddMilliseconds(4219), out _));
        Assert.Equal(Hex("3c02000041a801006b02000000000000"), Answer(start.NewState, 0x23C, 0x26B, T0.AddMilliseconds(11391), out _));
    }

    [Fact]
    public void ClearingA_ReportsTheRunningTimer_Live0452()
    {
        
        
        var sel = KharaTitleStore.Decide(new KharaTitleState(0x23C, 0x22A, null, null), 0x244, 0x22A, T0, Any).NewState;
        var early = KharaTitleStore.Decide(sel, 0, 0x22A, T0.AddMilliseconds(6200), Any);
        Assert.True(early.Accepted);
        Assert.Equal(Hex("00000000eb2101002a02000000000000"), Answer(sel, 0, 0x22A, T0.AddMilliseconds(45781), out _));
    }

    [Fact]
    public void ClearWithNoTimer_IsAllZero_Live6622c()
    {
        
        Assert.Equal(Hex("00000000000000000000000000000000"), Answer(default, 0, 0, T0, out _));
    }

    [Fact]
    public void SameAAfterTheTimerRanOut_RestartsAt120000_Archive007b335e()
    {
        
        
        var sel = KharaTitleStore.Decide(default, 0x23C, 0x268, T0, Any).NewState;
        var later = T0.AddMinutes(7).AddSeconds(9);
        Assert.Equal(Hex("3c020000c0d401006b02000000000000"), Answer(sel, 0x23C, 0x26B, later, out var d));
        Assert.Equal(later, d.NewState.ATimerStartUtc);
    }

    [Fact]
    public void ArchiveFb30ReSelect_IsReproduced_OnThePlayClock()
    {
        
        
        
        
        
        
        
        const uint charId = 0x7FB0_FB30;
        static DateTime At(int h, int m, int s, int ms) => new(2026, 7, 18, h, m, s, ms, DateTimeKind.Utc);
        try
        {
            KharaTitleClock.Seed(charId, null, At(18, 31, 20, 661));
            KharaTitleClock.Resume(charId, At(18, 31, 20, 661));
            KharaTitleClock.OnMapLoaded(charId, KharaTitleClock.PausedMapType, At(18, 31, 21, 591)); 
            var sel = KharaTitleStore.Decide(new KharaTitleState(0x24C, 0x1F1, null, null), 0x24C, 0x22B,
                KharaTitleClock.PlayNow(charId, At(18, 37, 35, 529)), Any).NewState;
            KharaTitleClock.Pause(charId, At(18, 59, 54, 491));                                       
            KharaTitleClock.Resume(charId, At(18, 59, 54, 682));
            var reSelect = KharaTitleClock.PlayNow(charId, At(19, 0, 52, 932));
            var body = Answer(sel, 0x24C, 0x1F1, reSelect, out var d);
            Assert.Equal(Hex("4c02000036f10000f101000000000000"), body);
            Assert.Equal(Hex("11269508fffffffff1010000"), KharaTitleSelectPackets.Build2592(0x08952611u, d));
            Assert.Equal(sel.ATimerStartUtc, d.NewState.ATimerStartUtc); 
        }
        finally
        {
            KharaTitleClock.Forget(charId);
        }
    }

    [Fact]
    public void IdenticalReSelectOnANonZeroB_InsideTenSeconds_Refused_Live0452()
    {
        
        
        
        var first = Answer(new KharaTitleState(0, 0x22A, null, null), 0x23C, 0x22A, T0, out var d1);
        Assert.Equal(Hex("3c020000c0d401002a02000000000000"), first);
        var again = Answer(d1.NewState, 0x23C, 0x22A, T0.AddMilliseconds(1641), out var d2);
        Assert.Equal(Hex("3c020000a72000002a020000f7540000"), again);
        Assert.False(d2.Accepted);
        Assert.Equal(d1.NewState, d2.NewState);
    }

    [Fact]
    public void IdenticalReSelect_IsFree_WithBZero_OrAfterTenSeconds()
    {
        
        
        var zero = Answer(default, 0, 0, T0, out var z1);
        Assert.Equal(Hex("00000000000000000000000000000000"), Answer(z1.NewState, 0, 0, T0.AddMilliseconds(297), out var z2));
        Assert.True(z2.Accepted);
        Assert.Equal(Hex("00000000000000000000000000000000"), zero);
        
        
        var sel = KharaTitleStore.Decide(new KharaTitleState(0, 0x22A, null, null), 0x244, 0x22A, T0, Any).NewState;
        var clear = KharaTitleStore.Decide(sel, 0, 0x22A, T0.AddMilliseconds(5172), Any);
        Assert.True(clear.Accepted);
        Assert.Equal(Hex("00000000eb2101002a02000000000000"), Answer(clear.NewState, 0, 0x22A, T0.AddMilliseconds(45781), out _));
    }

    [Fact]
    public void TitleNotWearable_TenThousandRefusal()
    {
        
        
        
        var state = new KharaTitleState(0x24C, 0x22B, T0.AddMinutes(-6), T0.AddMinutes(-6));
        var body = Answer(state, 0x256, 0x22B, T0, out var d, id => id != 0x256);
        Assert.Equal(Hex("56020000102700002b020000f7540000"), body);
        Assert.Equal(state, d.NewState);
    }

    [Fact]
    public void MayWear_NeedsACompletedKharaThatGrantsATitle()
    {
        var rows = LoadKharaRows();
        var titles = KharaTitleStore.TitleGrantingKharas(rows);
        
        uint noTitle = rows.GroupBy(k => k.Quest_ID).First(g => g.All(k => k.Reward_Title_Id == 0)).Key;
        Assert.True(KharaTitleStore.MayWear(0x23C, new uint[] { 0x23C }, titles));
        Assert.False(KharaTitleStore.MayWear(0x23C, Array.Empty<uint>(), titles));
        Assert.False(KharaTitleStore.MayWear(noTitle, new[] { noTitle }, titles));
        Assert.Same(titles, KharaTitleStore.TitleGrantingKharas(rows));
    }

    [Fact]
    public void MayWear_FindsATitleOnALaterRow_Khara410_Live3d94()
    {
        var rows = LoadKharaRows();
        var titles = KharaTitleStore.TitleGrantingKharas(rows);
        
        
        uint[] laterRow = { 400, 409, 410, 412, 413, 414, 415 };
        foreach (var id in laterRow)
        {
            var ofId = rows.Where(k => k.Quest_ID == id).ToList();
            Assert.Equal(0, ofId[0].Reward_Title_Id);
            Assert.Contains(ofId, k => k.Reward_Title_Id != 0);
            Assert.True(KharaTitleStore.MayWear(id, new[] { id }, titles));
        }
        Assert.Contains(rows, k => k.Quest_ID == 410 && k.Reward_Title_Id == 155);
        
        foreach (var id in new uint[] { 420, 421 })
        {
            Assert.Contains(rows, k => k.Quest_ID == id && k.Reward_Title_Id != 0);
            Assert.False(KharaTitleStore.MayWear(id, new[] { id }, titles));
        }

        
        
        var completed = new uint[] { 410 };
        var body = Answer(default, 410, 0, T0, out var d, id => KharaTitleStore.MayWear(id, completed, titles));
        Assert.True(d.Accepted);
        Assert.Equal(Hex("9a010000c0d401000000000000000000"), body);
    }

    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static List<KharaInfo> LoadKharaRows()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Khara_Info.json")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Khara_Info.json");
        return JsonSerializer.Deserialize<List<KharaInfo>>(File.ReadAllText(Path.Combine(dir.FullName, "data", "Khara_Info.json")),
            DataJsonOptions)!;
    }
}
