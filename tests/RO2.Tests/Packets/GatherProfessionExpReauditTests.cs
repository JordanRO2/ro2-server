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
/// Profession experience of a gather (2526). Expected hex strings are reference frames. Shipped tables are
/// found by walking up from the test output directory.
/// </summary>
public class GatherProfessionExpReauditTests
{
    
 /// <summary>The 2526 builder against a reference gather frame (experience 268, gain 86).</summary>
    [Fact]
    public void ProJobExpChange2526_IsTheDumpGatherFrame()
        => Assert.Equal("011818001be4000000000c010000000000005600000000000000",
            Hex(ProfessionExpPackets.BuildProJobExpChange2526(268, 86)));

 /// <summary>
 /// A gather's base gain is the FuncAction Action_Value1 of an Action_Type 11 row (none for other rows). A VVIP
 /// effect's trait-76 rate of 3,000 raises it to Action_Value1 or floor(gain x 1.3); for example 86 becomes
 /// 111 and 5 becomes 6.
 /// </summary>
    [Fact]
    public async Task GatherProfessionExp_IsActionValue1_RaisedByTrait76()
    {
        var store = await LoadStoreAsync("FuncAction.json", "Effect_Table.json");
        var gathers = store.FuncAction.Where(r => r.Action_Type == FuncObjectActions.ProfessionExpActionType).ToList();
        Assert.NotEmpty(gathers);
        foreach (var row in gathers)
            Assert.Equal(row.Action_Value1, FuncObjectActions.ProfessionExpOf(store, row.ID));
        var portal = store.FuncAction.First(r => r.Action_Type == FuncObjectActions.PortalActionType);
        Assert.Equal(0u, FuncObjectActions.ProfessionExpOf(store, portal.ID));
        Assert.Equal(new uint[] { 5, 42, 86, 133, 183, 256 }, gathers.Select(r => r.Action_Value1).Distinct().Order().ToArray());

        var effects = new EffectService(players: null!, broadcast: null!, data: store, npcState: null!, loot: null!,
            scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create());
        var plain = new PlayerState();
        Assert.Equal(0d, effects.ProJobExpRateOf(plain));
        var vvip = new PlayerState();
        vvip.ActiveEffects.Add(new ActiveEffect { EffectId = 105001 });
        var rate = effects.ProJobExpRateOf(vvip);
        Assert.Equal(3000d, rate);
        Assert.Equal(new uint[] { 111, 237, 172, 54, 6 },
            new uint[] { 86, 183, 133, 42, 5 }.Select(v => EffectService.ApplyExpRate(v, rate)).ToArray());
        Assert.Equal(86u, EffectService.ApplyExpRate(86, effects.ProJobExpRateOf(plain)));
    }
}
