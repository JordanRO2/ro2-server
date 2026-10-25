using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Skill damage element lists: the 1509 echoes and the per-type 2718 elements. Shipped data tables are found by
/// walking up from the test output directory.
/// </summary>
public class SkillDamageElementsReauditTests
{
    
    private static int? ShippedSkillType(uint skillId) =>
        SkillDamagePackets.ParseSkillTypes(File.ReadAllBytes(Path.Combine(RequireDataDirectory(), "Skill_Table_Base.json")))
            .TryGetValue(skillId / 100, out var t) ? t : null;

 /// <summary>
 /// A buff (Skill_Type 4) that the caster applies to itself. The 2718 echoes the request's position and index fields
 /// (index 0), lists the caster with its HP and SP, and carries no effect record.
 /// </summary>
    [Fact]
    public void SkillDamage2718_BuffOnSelf_IsTheArchiveFrame()
    {
        var request = SkillDamageHandler.ParseRequest(Convert.FromHexString(
            "2f0000001f5e030001110efc0634993c441c3e3c44014a5c425c557abf324256be000000000202000000000000000000000000"));
        Assert.Equal(new uint[] { 0x06FC0E11 }, request.Targets);
        var type = ShippedSkillType(request.SkillId);
        Assert.Equal(4, type);
        var caster = new PlayerState { EntityId = 0x06FC0E11, MapId = 1100, Hp = 53434, Mp = 11704 };
        var players = new PlayerStateManager(new NoDirtySink());
        var pc = SkillDamagePcElements.ResolvePc(players, caster, request.Targets[0]);
        Assert.Same(caster, pc);
        var element = SkillDamagePcElements.PcElement(type, caster, pc!);
        Assert.NotNull(element);

        var body = SkillDamagePackets.BuildSkillDamage2718(caster.EntityId, request.SkillId, request.PrimaryTarget,
            new[] { element.Value }, request.X, request.Y, request.Z, request.CastType, request.SkillEffectIndex, request.Unknown34);
        Assert.Equal("0147" + "47004be4000000001f5e0300110efc0601110efc0634993c441c3e3c44014a5c4202020000000000000000000001"
            + "110efc06bad00000b82d000000000000000000000000000000", Hex(body));
    }

 /// <summary>A type-10 cast that names one PC gets a single all-zero element.</summary>
    [Fact]
    public void SkillDamage2718_Type10AtOnePc_IsTheArchiveFrame()
    {
        var request = SkillDamageHandler.ParseRequest(Convert.FromHexString(
            "2f000000f8ab1e0001114def06000000000000000000000000dde8423f79f8253f000000000000000000000000000000000000"));
        var type = ShippedSkillType(request.SkillId);
        Assert.Equal(10, type);
        Assert.True(SkillDamagePcElements.IsSingleZeroElementCast(type, request.Targets));
        var body = SkillDamagePackets.BuildSkillDamage2718(0x06EF4D11, request.SkillId, request.PrimaryTarget,
            new[] { SkillDamagePcElements.SingleZeroElement() }, request.X, request.Y, request.Z, request.CastType,
            request.SkillEffectIndex, request.Unknown34);
        Assert.Equal("0147" + "47004be400000000f8ab1e00114def0601114def06000000000000000000000000000000000000000000000000"
            + "0100000000000000000000000000000000000000000000000000", Hex(body));
    }

 /// <summary>The skill index occupies bytes 43 and 44, and the hit count is the u16 at offset 45.</summary>
    [Fact]
    public void SkillDamageRequest_ReadsTheCountAt45AndTheIndexAt43()
    {
        var request = SkillDamageHandler.ParseRequest(Convert.FromHexString(
            "3300000086d701000000000000ba57324437a306445f0f6f420000000000000000000000000001000000000000000101010100132e1100"));
        Assert.Equal(120710u, request.SkillId);
        Assert.Equal(0x0101, request.SkillEffectIndex);
        Assert.Equal(1ul, request.Unknown34);
        Assert.Equal(new uint[] { 0x00112E13 }, request.Targets);
        Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(Convert.FromHexString("ba573244")), request.X);
    }

 /// <summary>
 /// Element rules by Skill_Type: a buff lists the caster and other PCs, a debuff lists only the others, and a heal or
 /// attack lists none. Only attack (or unknown) types damage monsters, and a debuff lists them undamaged.
 /// </summary>
    [Fact]
    public void SkillDamageElements_FollowTheSkillType()
    {
        var caster = new PlayerState { EntityId = 0x06FC0E11, Hp = 10, Mp = 70000 };
        var other = new PlayerState { EntityId = 0x06FC0F11, Hp = -5, Mp = 3 };
        Assert.Equal(new SkillDamageHit(0x06FC0E11, 10, 65535, null), SkillDamagePcElements.PcElement(4, caster, caster));
        Assert.Equal(new SkillDamageHit(0x06FC0F11, 0, 3, null), SkillDamagePcElements.PcElement(4, caster, other));
        Assert.Null(SkillDamagePcElements.PcElement(2, caster, caster));
        Assert.NotNull(SkillDamagePcElements.PcElement(2, caster, other));
        Assert.Null(SkillDamagePcElements.PcElement(1, caster, other));
        Assert.Null(SkillDamagePcElements.PcElement(3, caster, other));
        Assert.Null(SkillDamagePcElements.PcElement(null, caster, other));
        Assert.True(SkillDamagePcElements.DamagesNpcs(1));
        Assert.True(SkillDamagePcElements.DamagesNpcs(null));
        Assert.False(SkillDamagePcElements.DamagesNpcs(4));
        Assert.True(SkillDamagePcElements.ListsNpcVitals(2));
        Assert.False(SkillDamagePcElements.IsSingleZeroElementCast(10, new uint[] { 0x00112E13 }));
        Assert.False(SkillDamagePcElements.IsSingleZeroElementCast(10, new uint[] { 0x06FC0E11, 0x06FC0F11 }));
    }
}
