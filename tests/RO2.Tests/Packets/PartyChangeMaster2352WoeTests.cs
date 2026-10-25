using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1387 ReqPartyChangeMaster refused on a WoE map (PartyChangeMasterHandler): 2352 with result 24324 and no
/// leader change, checked against the expected refusal frame and the shipped data/Map_List.json.
/// </summary>
public class PartyChangeMaster2352WoeTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    // A leader on 3101 asks to hand the party to GID 0x0DE0B6B3A79883F2 and gets this 2352 each time, with no 2365.
    // The leader stands on 3101 (its 2361 party record +35 = 3101); the member is online on 1100.
    private const string Retail1387 = "0110100026e311fc8907f28398a7b3b6e00d";
    private const string Retail2352Woe = "010a0a0027e311fc8907045f";
    private const ulong TargetGid = 0x0DE0B6B3A79883F2UL;
    private const ulong RequesterGid = 0x0DE0B6B3A7E7D981UL; // the GID that became leader through a 2365

    [Fact]
    public void WoeRefusal_IsRetailsFrame()
    {
        Assert.Equal(Convert.FromHexString(Retail2352Woe), PartyAnswerPackets.BuildResultAck(
            PartyStatePackets.MsgTagOnPartyChangeMaster, 0x0789FC11u, PartyAnswerPackets.ResultChangeMasterWoeMap));
    }

    [Theory]
    [InlineData(3101u, 1100u, true)]   // Prontera Fortress [WoE] -> Prontera
    [InlineData(3201u, 1300u, true)]   // Morroc Fortress [WoE], the other Map_Type 4 row
    [InlineData(3101u, 3101u, false)]  // both in the same WoE map: what string 11342 allows
    [InlineData(1100u, 3101u, false)]  // a requester outside WoE: no frame, not refused
    [InlineData(1100u, 1300u, false)]
    public async Task WoeRule_ReadsMapTypeFromTheShippedMapList(uint requesterMap, uint targetMap, bool refused)
    {
        var data = await LoadShippedMapListAsync();
        Assert.Equal(PartyChangeMasterHandler.WoeMapType, data.GetMap(3101)!.Map_Type);
        Assert.Equal(refused, PartyChangeMasterHandler.RefusedAsWoe(data, requesterMap, targetMap));
    }

    /// <summary>
    /// End to end through a real ProudServer: a 1387 from a leader on 3101 naming a member on 1100 is
    /// answered with exactly the expected 2352 and changes nothing. Once the leader is on 1100 the same request goes
    /// through (2352 result 0), and the member's FIRST frame is that 2365, so the refusal sent it nothing.
    /// </summary>
    [Fact]
    public async Task RefusedOnWoeMap_ThenDelegatedOnceOffIt()
    {
        await using var server = new ProudServer("Test", NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        using var leaderClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var memberClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await leaderClient.ConnectAsync(server.LocalEndPoint!);
        await memberClient.ConnectAsync(server.LocalEndPoint!);
        var deadline = DateTime.UtcNow + Generous;
        while (server.SessionCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(2, server.SessionCount);

        ProudSession SessionOf(Socket c) =>
            server.GetSessions().Single(s => s.RemoteEndPoint!.Port == ((IPEndPoint)c.LocalEndPoint!).Port);
        var leaderSession = SessionOf(leaderClient);
        var memberSession = SessionOf(memberClient);
        leaderSession.Crypto.SetRc4Key(Rc4Key);
        leaderSession.Handshake = HandshakeState.Connected; // RMIs are dispatched only after the handshake (S-H5)

        var players = new PlayerStateManager(new NoPersistence());
        var leader = new PlayerState
            { SessionId = leaderSession.SessionId, Name = "Namazuko", CharacterUid = RequesterGid, EntityId = 0x0789FC11u, MapId = 3101 };
        var member = new PlayerState
            { SessionId = memberSession.SessionId, Name = "buddyy", CharacterUid = TargetGid, EntityId = 0x07894A11u, MapId = 1100 };
        players.AddPlayer(leader.SessionId, leader);
        players.AddPlayer(member.SessionId, member);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(server);
        var parties = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var party = new PartyState
        {
            PartyId = 0x0D05, LeaderSessionId = leader.SessionId, MemberSessionIds = [leader.SessionId, member.SessionId],
        };
        party.Members[leader.SessionId] = leader;
        party.Members[member.SessionId] = member;
        party.FrameSlots[leader.SessionId] = 1;
        party.FrameSlots[member.SessionId] = 2;
        parties.Track(party);

        var services = new ServiceCollection()
            .AddSingleton(parties)
            .AddSingleton(await LoadShippedMapListAsync())
            .BuildServiceProvider();
        var handler = new PartyChangeMasterHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PartyChangeMasterHandler>.Instance, players, broadcast);
        server.PacketReceived += (s, id, payload) => id == handler.Opcode ? handler.HandleAsync(s, payload) : Task.CompletedTask;

        // On the fortress: the refusal, and the leader stays.
        await leaderClient.SendAsync(Encrypted(Request1387(1, Convert.FromHexString(Retail1387))), SocketFlags.None);
        var (op, body) = await ReadInternalRmiAsync(leaderClient);
        Assert.Equal(2352, op);
        Assert.Equal(Convert.FromHexString(Retail2352Woe), body);
        Assert.Equal(leader.SessionId, party.LeaderSessionId);

        // Off it: delegated. 2352 result 0 to the leader; the member's first frame is the 2365 naming it.
        leader.MapId = 1100;
        await leaderClient.SendAsync(Encrypted(Request1387(2, Convert.FromHexString(Retail1387))), SocketFlags.None);
        (op, body) = await ReadInternalRmiAsync(leaderClient);
        Assert.Equal(2352, op);
        Assert.Equal(PartyAnswerPackets.BuildResultAck(PartyStatePackets.MsgTagOnPartyChangeMaster, 0x0789FC11u), body);
        (op, body) = await ReadInternalRmiAsync(memberClient);
        Assert.Equal(2365, op);
        Assert.Equal(PartyStatePackets.BuildOnPartyNotifyChangeMaster2365(PartyAnswerPackets.PartyHandle(party), TargetGid), body);
        Assert.Equal(member.SessionId, party.LeaderSessionId);
    }

    /// <summary>A GameDataStore holding only the shipped data/Map_List.json (every loader tolerates a missing file).</summary>
    private static async Task<GameDataStore> LoadShippedMapListAsync()
    {
        var source = Path.Combine(RequireDataDirectory(), "Map_List.json");
        var dir = Path.Combine(Path.GetTempPath(), "ro2-maplist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(source, Path.Combine(dir, "Map_List.json"));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Walks up from the test output path to the repository's data directory.</summary>
    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Map_List.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Map_List.json");
    }

    // ------------------------------------------------------------------------------------------
    // Transport helpers, as in Whisper2325NotFoundTests.

    /// <summary>A 64-byte test key (512-bit RC4, as the handshake installs).</summary>
    private static readonly byte[] Rc4Key = Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11)).ToArray();

    /// <summary>The plaintext of a counter-bearing RMI message: [u16 counter][0x01][u16 1387][body].</summary>
    private static byte[] Request1387(ushort counter, byte[] body)
    {
        var plain = new byte[5 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(plain, counter);
        plain[2] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(3), 1387);
        body.CopyTo(plain, 5);
        return plain;
    }

    /// <summary>[0x25][02 = RC4][CompactLen n][RC4(plaintext)], framed.</summary>
    private static byte[] Encrypted(byte[] plaintext)
    {
        using var crypto = new ProudNetCrypto();
        crypto.SetRc4Key(Rc4Key);
        var cipher = crypto.Rc4Decrypt(plaintext)!; // symmetric: re-keyed per message
        var header = cipher.Length < 128
            ? new byte[] { 0x25, 0x02, 0x01, (byte)cipher.Length }
            : new byte[] { 0x25, 0x02, 0x02, (byte)cipher.Length, (byte)(cipher.Length >> 8) };
        return ProudFrameCodec.EncodeFrame([.. header, .. cipher]);
    }

    /// <summary>One S2C [13 57][CompactLen n][0x01][u16 rmiId][body] off a client socket.</summary>
    private static async Task<(ushort RmiId, byte[] Body)> ReadInternalRmiAsync(Socket client)
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
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);

        static async Task<byte[]> ReadExactAsync(Socket s, int n)
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
    }

    private sealed class NoPersistence : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }
}
