using Microsoft.Extensions.Logging.Abstractions;
using NameFilterRow = RO2.GameData.Tables.NameFilter;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat.Handlers.Rmi;
using RO2.Server.Chat;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>Guild Basic Robe pick: 1633 answered by 2928 and 2927.</summary>
public class GuildRobePickReauditTests
{
    
    [Fact]
    public void BasicRobePick_Date_IsTheLiteral2000_11_11Sentinel_WhateverTheTimeZone()
    {
        
        Assert.Equal("889349ea2efde140", Hex(BitConverter.GetBytes(GuildRobeChangedPackets.BasicRobeSelectedDate)));
        var record = GuildRobeChangedPackets.BasicRobeSelectedRecord();
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(record));                     
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12)));          
        Assert.Equal(0x40E1FD2Eu, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16))); 
        Assert.Equal(GuildRobeRecord.BasicRobeColor, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20)));
        Assert.Equal(0x80, record[24]);
    }
}
