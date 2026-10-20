using System.Text.Json;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-exact checks against reference frames for the guardian transform (1413 to 2530, 2529 and 2531, with 2532 at
/// the end), for 3033 AnsReviewPC with the reviewed PC's active effects, and for the refusal rule of 1600
/// ReqSetPresentPassword.
/// </summary>
public class TransModeAndReviewTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    
    [Theory]
    
    [InlineData(0x06ff4911u, 5040003u, (byte)3, "0111110026e4000000001149ff0683e74c0003")]
    
    [InlineData(0x08130911u, 5010001u, (byte)1, "0111110026e4000000001109130851724c0001")]
    
    [InlineData(0x084c2511u, 5030002u, (byte)2, "0111110026e40000000011254c0872c04c0002")]
    public void NotifyTransModeStart2531_ReproducesRetailFrames(uint handle, uint skill, byte grade, string retail)
        => Assert.Equal(retail, Hex(TransModeNotifyPackets.BuildNotifyTransModeStart2531(handle, skill, grade)));

    [Fact]
    public void NotifyTransModeEnd2532_ReproducesRetailFrame()
    {
        
        Assert.Equal("010c0c0027e4000000001149ff06", Hex(TransModeNotifyPackets.BuildNotifyTransModeEnd2532(0x06ff4911u)));
    }

    [Fact]
    public void AckTransModeStartRefused2530_ReproducesRetailRepeatAnswer()
    {
        
        
        Assert.Equal("010a0a0025e4000000007c58",
            Hex(TransModeNotifyPackets.BuildAckTransModeStartRefused2530(TransModeNotifyPackets.ResultAlreadyTransformed)));
        
        Assert.Equal("010a0a0025e4000000000000", Hex(ActorStatePackets.BuildOnAckTransModeStart()));
    }

    [Theory]
    
    [InlineData((byte)4, new uint[] { 511000040, 511000041, 511000042 }, 5040003u, (byte)3)]
    [InlineData((byte)1, new uint[] { 511000010 }, 5010001u, (byte)1)]
    [InlineData((byte)3, new uint[] { 511000030, 511000031 }, 5030002u, (byte)2)]
    
    [InlineData((byte)2, new uint[] { 511000020, 511000021, 123 }, 5020002u, (byte)2)]
    public async Task ResolveTransBuff_MatchesRetailFromProJobTypeAndCompletedQuests(byte profession, uint[] completed,
        uint skill, byte grade)
    {
        var rows = await LoadProJobTypeAsync();
        Assert.Equal((skill, grade), TransModeNotifyPackets.ResolveTransBuff(rows, profession, completed));
    }

    [Theory]
    [InlineData((byte)1, new uint[0])]                  
    [InlineData((byte)1, new uint[] { 511000020 })]     
    [InlineData((byte)0, new uint[] { 511000010 })]     
    [InlineData((byte)9, new uint[] { 511000010 })]     
    public async Task ResolveTransBuff_NoGrade_GivesNull(byte profession, uint[] completed)
    {
        var rows = await LoadProJobTypeAsync();
        Assert.Null(TransModeNotifyPackets.ResolveTransBuff(rows, profession, completed));
    }

    [Fact]
    public void TryRecordTransModeStart_RefusesARepeatWhileTheTransformRuns()
    {
        const uint characterId = 0xA10A1413u; 
        var t0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(TransChargePackets.TryRecordTransModeStart(characterId, t0, out var end));
        Assert.Equal(t0.AddSeconds(TransChargePackets.TransModeTimeSeconds), end);

        
        Assert.False(TransChargePackets.TryRecordTransModeStart(characterId, t0.AddMilliseconds(143), out var running));
        Assert.Equal(end, running);
        Assert.True(TransChargePackets.IsLatestTransform(characterId, end));

        
        Assert.True(TransChargePackets.TryRecordTransModeStart(characterId, end.AddSeconds(1), out var next));
        Assert.False(TransChargePackets.IsLatestTransform(characterId, end));
        Assert.True(TransChargePackets.IsLatestTransform(characterId, next));
    }

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static async Task<IReadOnlyList<ProJobType>> LoadProJobTypeAsync()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "ProJob_Type.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        await using var file = File.OpenRead(Path.Combine(dir.FullName, "data", "ProJob_Type.json"));
        var rows = await JsonSerializer.DeserializeAsync<List<ProJobType>>(file, CaseInsensitive);
        Assert.NotNull(rows);
        return rows;
    }

    
 /// <summary>
 /// A full 3033 AnsReviewPC frame: the header, the PC record, four effects, six worn items and the 112-byte stat block.
 /// </summary>
    private const string Retail3033 =
        "340400000000011125490bd89aeea7b3b6e00d00008c06de4334d1b0438b89474101010608ff0004000000060eff0001d4100000d4100000"
        + "e5010000e50100000000d040dbfa2c4010000600000d49006c00480049006c00340031006c0049004e0031006c0049004020440600020200"
        + "000000000400718e0100010001894cdd030000000089a49b004dea9fc5329a0100010001d3b54113010000009539a0000d4588c5f29a0100"
        + "01000162bd1c0000000000c33aa0001125490b64340300010001000000000000000089d61e001125490b06001e50d68898c4270ef935bd00"
        + "1c1d010002000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "00001f50d68898c4270e3978cc001c1d01000300000000000000000000000000000000000000000000000000000000000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "000000000000000000000000000000002050d68898c4270e79badb001c1d0100040000000000000000000000000000000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000002150d68898c4270eb9fcea001c1d010005000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000002250d68898c4270eb9f3ad00"
        + "1b1e010006000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "0000c8b5b60ca83d040e29f6c100000001001000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "000000000000000000000000000000000000000000008e260000101000000000000000000000000000000000000000000000000000000000"
        + "0000000000000000d4100000d4100000e501e5012401c80000000000870020000000340021002e000000000000002e00200016001d002d00"
        + "00000000000000000000000000000000";

    [Fact]
    public void AnsReviewPC3033_FullForm_PutsTheActiveEffectsWhereRetailDoes()
    {
        var frame = Convert.FromHexString(Retail3033);
        const int recordEnd = 116, effectCount = 4, gearStart = recordEnd + 2 + 23 * effectCount, gearCount = 6;
        Assert.Equal(effectCount, BitConverter.ToUInt16(frame, recordEnd));
        Assert.Equal(gearCount, BitConverter.ToUInt16(frame, gearStart));
        Assert.Equal(gearStart + 2 + 126 * gearCount, frame.Length - 112);
        var gear = Enumerable.Range(0, gearCount)
            .Select(i => frame.AsSpan(gearStart + 2 + 126 * i, 126).ToArray()).ToList();
        var stats = frame[^112..];

        
        
        
        var now = new DateTime(2026, 7, 17, 21, 6, 38, 545, DateTimeKind.Utc);
        var target = new PlayerState { EntityId = 0x0b492511u };
        target.ActiveEffects.Add(new ActiveEffect
        {
            EffectId = 210020, EffectValue = 1, StackCount = 1, SourceSkillId = 2021001, CasterHandle = 0x0b492511u,
        });
        target.ActiveEffects.Add(new ActiveEffect
        {
            EffectId = 105010, EffectValue = 1, StackCount = 1, SourceSkillId = 10500501, CasterHandle = 0xc588450du,
            ExpiresAt = now.AddMilliseconds(4_618_040_787),
        });
        target.ActiveEffects.Add(new ActiveEffect
        {
            EffectId = 102001, EffectValue = 1, StackCount = 1, SourceSkillId = 10200201, CasterHandle = 0xc59fea4du,
            ExpiresAt = now.AddMilliseconds(64_834_697),
        });
        target.ActiveEffects.Add(new ActiveEffect
        {
            EffectId = 105202, EffectValue = 1, StackCount = 1, SourceSkillId = 10500803, CasterHandle = 0x0b492511u,
            ExpiresAt = now.AddMilliseconds(1_883_490),
        });

        var data = ReviewPCHandler.AssembleReviewData(frame.AsSpan(7, recordEnd - 7), target, gear, stats, now);
        Assert.Equal(Hex(frame[7..]), Hex(data));
        Assert.Equal(Retail3033, Hex(ReviewPCHandler.BuildAnsReviewPC3033(data)));
    }

    [Fact]
    public void AnsReviewPC3033_NotAllowedForm_IsRetailsSevenBytes()
    {
        
        Assert.Equal("03000000000000", Hex(ReviewPCHandler.BuildAnsReviewPC3033(null)));
    }

    
    [Fact]
    public void SetPresentPassword_Refusal()
    {
        
        Assert.Null(SetPresentPasswordHandler.Refusal(hasPassword: false, oldMatches: false, "0000"));
        
        Assert.Null(SetPresentPasswordHandler.Refusal(hasPassword: true, oldMatches: true, "1234"));
        
        Assert.Equal(SetPresentPasswordHandler.ResultIncorrectPassword,
            SetPresentPasswordHandler.Refusal(hasPassword: true, oldMatches: false, "1234"));
        Assert.Equal((ushort)24206, SetPresentPasswordHandler.ResultIncorrectPassword);
        
        Assert.Equal(SetPresentPasswordHandler.ResultRefused,
            SetPresentPasswordHandler.Refusal(hasPassword: false, oldMatches: false, "12"));
    }
}
