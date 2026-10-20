using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>Refusal (2695) of a normal attack (1498) on a PC target.</summary>
public class NormalAttackRefusalReauditTests
{
    
 /// <summary>
 /// The refusal code depends on the target. Refused normal attacks on a PC target carry code 20122 and never 22301.
 /// A dead NPC also gets 20122. An unknown non-PC handle and a live, non-hostile NPC get 22301.
 /// </summary>
    [Theory]
    [InlineData(true, false, 0x06EE6E11u, 20122)]  
    [InlineData(true, false, 0x0000D413u, 22301)]  
    [InlineData(true, false, 0x00001214u, 22301)]  
    [InlineData(false, true, 0x0000D413u, 20122)]  
    [InlineData(false, false, 0x0000D413u, 22301)] 
    public void RefusalCode_IsRetailsPerTarget(bool noNpcState, bool npcDead, uint target, int code)
        => Assert.Equal((ushort)code, NormalAttackStartHandler.RefusalCode(noNpcState, npcDead, target));
}
