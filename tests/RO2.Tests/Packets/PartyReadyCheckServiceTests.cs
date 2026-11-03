using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.ProudNet;
using RO2.ReadyChecks.Domain;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The host side of the party ready check: effect to frame translation (2697 to 2700), and the whole 1500/1501 flow
/// over real sessions, including the send order, a repeated vote, and the answer-window timeout settle.
/// </summary>
public class PartyReadyCheckServiceTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private const ulong LeadGid = 0x0DE0B6B3A7ECF18DUL;

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Translator_ReproducesTheFourFramesFromTheEffects()
    {
        Assert.Equal((2698, "0000"), Frame(new AckOk()));
        Assert.Equal((2698, "6f3d"), Frame(new AlreadyChecking()));
        Assert.Equal((2700, "6d3d"), Frame(new AlreadyVoted()));
        Assert.Equal((2699, "0110100040e4000000008df1eca7b3b6e00d"), Frame(new NotifyStart(3, LeadGid)));
        Assert.Equal((2697, "0400000001000000"), Frame(new CheckResult(1, 0, [])));
        Assert.Equal((2697, Hex(PartyReadyCheckPackets.BuildNfyPartyFightingTrimResult2697(2, 1, ["Azuv"]))),
            Frame(new CheckResult(2, 1, ["Azuv"])));
        Assert.Null(PartyReadyCheckFrames.ToFrame(new Abandoned(), LeadGid));

        static (int, string) Frame(ReadyCheckEffect e)
        {
            var (op, body) = PartyReadyCheckFrames.ToFrame(e, LeadGid)!.Value;
            return (op, Hex(body));
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required ProudServer Server;
        public required Socket[] Clients;
        public required PlayerState[] Players;
        public required PartyReadyCheckService Service;
        public required ProudSession[] Sessions;
        public const uint PartyId = 0x0D05;

        public static async Task<Rig> CreateAsync(TimeSpan? window)
        {
            var server = new ProudServer("Test", NullLogger.Instance);
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var clients = new Socket[3];
            for (var i = 0; i < 3; i++)
            {
                clients[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await clients[i].ConnectAsync(server.LocalEndPoint!);
            }
            var deadline = DateTime.UtcNow + Generous;
            while (server.SessionCount < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.Equal(3, server.SessionCount);
            var sessions = clients.Select(c =>
                server.GetSessions().Single(s => s.RemoteEndPoint!.Port == ((IPEndPoint)c.LocalEndPoint!).Port)).ToArray();

            var players = new PlayerStateManager(new NoPersistence());
            var states = new[] { "Lead", "Azuv", "Bee" }.Select((n, i) => new PlayerState
            {
                SessionId = sessions[i].SessionId, CharacterId = (uint)(i + 1), CharacterUid = LeadGid + (ulong)i, Name = n, MapId = 1100,
            }).ToArray();
            foreach (var p in states) players.AddPlayer(p.SessionId, p);
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            var parties = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
            var party = new PartyState { PartyId = PartyId, LeaderSessionId = states[0].SessionId, MemberSessionIds = [.. states.Select(s => s.SessionId)] };
            foreach (var p in states) party.Members[p.SessionId] = p;
            parties.Track(party);

            var roster = new PartyRosterAdapter(parties);
            var service = window is { } w
                ? new PartyReadyCheckService(roster, broadcast, NullLogger<PartyReadyCheckService>.Instance, w)
                : new PartyReadyCheckService(roster, broadcast, NullLogger<PartyReadyCheckService>.Instance);
            return new Rig { Server = server, Clients = clients, Players = states, Service = service, Sessions = sessions };
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in Clients) c.Dispose();
            await Server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Flow_LeaderStarts_MembersVote_AndTheResultFollowsTheLastVote()
    {
        await using var rig = await Rig.CreateAsync(null);
        var (lead, azuv, bee) = (rig.Clients[0], rig.Clients[1], rig.Clients[2]);

        await rig.Service.StartAsync(Rig.PartyId, rig.Players[0], rig.Sessions[0]);
        Assert.Equal((2698, "0000"), await ReadAsync(lead));
        Assert.Equal((2699, Hex(PartyReadyCheckPackets.BuildNfyPartyFightingTrimStart2699(LeadGid))), await ReadAsync(azuv));
        Assert.Equal((2699, Hex(PartyReadyCheckPackets.BuildNfyPartyFightingTrimStart2699(LeadGid))), await ReadAsync(bee));

        // A second 1500 while it runs: 15727 to the leader only.
        await rig.Service.StartAsync(Rig.PartyId, rig.Players[0], rig.Sessions[0]);
        Assert.Equal((2698, "6f3d"), await ReadAsync(lead));

        await rig.Service.VoteAsync(Rig.PartyId, rig.Players[1], rig.Sessions[1], ReadyCheck.VoteReady);
        Assert.Equal((2698, "0000"), await ReadAsync(azuv));
        await rig.Service.VoteAsync(Rig.PartyId, rig.Players[1], rig.Sessions[1], 0);
        Assert.Equal((2700, "6d3d"), await ReadAsync(azuv));
        Assert.Equal(0, lead.Available);

        await rig.Service.VoteAsync(Rig.PartyId, rig.Players[2], rig.Sessions[2], 0);
        Assert.Equal((2698, "0000"), await ReadAsync(bee));
        Assert.Equal((2697, Hex(PartyReadyCheckPackets.BuildNfyPartyFightingTrimResult2697(1, 1, ["Bee"]))), await ReadAsync(lead));
    }

    [Fact]
    public async Task Flow_TimeoutSettlesTheSilentAsNotReady_AndASettledCheckIgnoresTheTimer()
    {
        await using var rig = await Rig.CreateAsync(TimeSpan.FromMilliseconds(300));
        var lead = rig.Clients[0];

        // Check 1: Bee never answers, the window settles it.
        await rig.Service.StartAsync(Rig.PartyId, rig.Players[0], rig.Sessions[0]);
        Assert.Equal((2698, "0000"), await ReadAsync(lead));
        await rig.Service.VoteAsync(Rig.PartyId, rig.Players[1], rig.Sessions[1], ReadyCheck.VoteReady);
        Assert.Equal((2697, Hex(PartyReadyCheckPackets.BuildNfyPartyFightingTrimResult2697(1, 1, ["Bee"]))), await ReadAsync(lead));

        // Check 2: everyone answers in time; the timer must then add nothing.
        await rig.Service.StartAsync(Rig.PartyId, rig.Players[0], rig.Sessions[0]);
        Assert.Equal((2698, "0000"), await ReadAsync(lead));
        await rig.Service.VoteAsync(Rig.PartyId, rig.Players[1], rig.Sessions[1], ReadyCheck.VoteReady);
        await rig.Service.VoteAsync(Rig.PartyId, rig.Players[2], rig.Sessions[2], ReadyCheck.VoteReady);
        Assert.Equal((2697, Hex(PartyReadyCheckPackets.BuildNfyPartyFightingTrimResult2697(2, 0, []))), await ReadAsync(lead));
        await Task.Delay(700);
        Assert.Equal(0, lead.Available);
    }

    /// <summary>One S2C [13 57][CompactLen n][0x01][u16 rmiId][body] off a client socket.</summary>
    private static async Task<(int RmiId, string Body)> ReadAsync(Socket client)
    {
        var head = await ReadExactAsync(client, 3);
        Assert.Equal(new byte[] { 0x13, 0x57 }, head[..2]);
        var lenBytes = await ReadExactAsync(client, head[2]);
        int len = head[2] switch
        {
            1 => lenBytes[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(lenBytes),
            _ => BinaryPrimitives.ReadInt32LittleEndian(lenBytes),
        };
        var data = await ReadExactAsync(client, len);
        Assert.Equal(0x01, data[0]);
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), Hex(data[3..]));
    }

    private static async Task<byte[]> ReadExactAsync(Socket s, int n)
    {
        var buf = new byte[n];
        for (int got = 0; got < n;)
        {
            int r = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None).AsTask().WaitAsync(Generous);
            if (r == 0) throw new EndOfStreamException();
            got += r;
        }
        return buf;
    }

    private sealed class NoPersistence : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }
}
