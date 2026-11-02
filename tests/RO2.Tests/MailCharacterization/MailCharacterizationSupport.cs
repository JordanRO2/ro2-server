using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Characters.Domain;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using Xunit;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Economy;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.MailCharacterization;

/// <summary>
/// B9 S1 rig: the real mail handlers and <see cref="MailService"/> over a loopback ProudNet session, with the Economy fakes
/// (<see cref="EconomyFixture"/>) as the stores. The tests pin what the host does TODAY (quirks included) so that a byte-identical
/// move of the mail rules keeps them green.
/// </summary>
internal sealed class MailRig : IAsyncDisposable
{
    public const uint Self = 1;
    public const uint SelfHandle = 0x07087E11;

    public required Harness Net { get; init; }
    public required EconomyFixture Fx { get; init; }
    public required PlayerStateManager Players { get; init; }
    public required BroadcastService Broadcast { get; init; }
    public required ServiceProvider Services { get; init; }
    public required PlayerState Player { get; init; }
    public ProudSession Session => Net.Sessions[0];
    public IServiceScopeFactory Scopes => Services.GetRequiredService<IServiceScopeFactory>();

    public static async Task<MailRig> StartAsync(long gold = 0, string name = "Sender", GameDataStore? store = null,
        bool withInventory = true, int sessions = 1, Func<EconomyFixture, ICharacterRepository>? characters = null,
        NpcStateManager? npcs = null)
    {
        var fx = new EconomyFixture();
        fx.AddOfflineCharacter(Self, gold, name: name);
        var net = await Harness.StartAsync(sessions);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var player = new PlayerState
        {
            SessionId = net.Sessions[0].SessionId, EntityId = SelfHandle, CharacterId = Self, Name = name,
            MapId = 1100, X = 10, Y = 10, MapLoaded = true, Gold = gold,
        };
        players.AddPlayer(player.SessionId, player);
        var sc = new ServiceCollection()
            .AddSingleton(fx.MailRepo)
            .AddSingleton(characters?.Invoke(fx) ?? fx.CharacterRepo);
        if (withInventory) sc.AddSingleton(fx.InventoryRepo);
        if (store != null) sc.AddSingleton(store);
        if (npcs != null) sc.AddSingleton(npcs);
        return new MailRig { Net = net, Fx = fx, Players = players, Broadcast = broadcast, Services = sc.BuildServiceProvider(), Player = player };
    }

    /// <summary>A second connected, in-world player (a recipient who is online).</summary>
    public PlayerState AddOnline(uint charId, string name, uint entityId)
    {
        Fx.AddOfflineCharacter(charId, 0, name: name);
        var p = new PlayerState
            { SessionId = Net.Sessions[1].SessionId, EntityId = entityId, CharacterId = charId, Name = name, MapLoaded = true };
        Players.AddPlayer(p.SessionId, p);
        return p;
    }

    public async Task<List<(ushort Op, byte[] Body)>> DrainAsync(int client = 0)
    {
        await Net.Sessions[client].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
        var frames = new List<(ushort, byte[])>();
        while (true)
        {
            var frame = await Net.Readers[client].NextAsync();
            if (frame.Op == ZoneS2COpcodes.OnLevelUp) return frames;
            frames.Add(frame);
        }
    }

    /// <summary>Sends a request through the real handler and returns every frame it left for the sender.</summary>
    public async Task<List<(ushort Op, byte[] Body)>> CallAsync(RO2.Network.Handlers.IRmiHandler handler, byte[] request)
    {
        await handler.HandleAsync(Session, request);
        return await DrainAsync();
    }

    public MailMessage Mail(uint receiver = Self, long gold = 0, uint item = 0, ulong itemUid = 0, int count = 1, byte kind = 0,
        DateTime? sentAt = null, bool read = false)
        => Fx.Mail.Seed(new MailMessage
        {
            ReceiverCharId = receiver, ReceiverName = "R", SenderCharId = 9, SenderName = "S", Title = "T", Body = "B",
            Gold = gold, AttachedItemId = item, AttachedItemUid = itemUid, AttachedItemCount = item == 0 ? 0 : count,
            Kind = kind, SentAt = sentAt ?? DateTime.UtcNow, IsRead = read,
        });

    public MailMessage Stored(ulong uid) => Fx.Mail.Mails.Single(m => m.Uid == uid);

    public ValueTask DisposeAsync()
    {
        Services.Dispose();
        return Net.DisposeAsync();
    }

    // ---- request builders (the C2S bodies the handlers parse) ----------------------------------------------------------

    /// <summary>1396/1397/1398/1457: [owner u32@4][mail uid u64@8].</summary>
    public static byte[] Ref(ulong uid, ushort tag = 0xE356)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p => { p.WriteUInt16(tag); p.WriteUInt32(SelfHandle); p.WriteUInt64(uid); });
        return pw.ToArray();
    }

    /// <summary>1393 ReqMailSend.</summary>
    public static byte[] Send(string recipient, string title = "T", long gold = 0, ulong itemUid = 0, byte itemCount = 0,
        string body = "", ulong? rawGold = null)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(0xE359);
            p.WriteUInt32(SelfHandle);
            p.WriteBytes(Fixed(recipient));
            p.WriteBytes(Fixed(title));
            p.WriteUInt64(rawGold ?? (ulong)gold);
            p.WriteUInt64(itemUid);
            p.WriteByte(0);                          // slot
            p.WriteByte(0);                          // container
            p.WriteByte(itemCount);
            p.WriteUInt16((ushort)body.Length);
            p.WriteBytes(Encoding.Unicode.GetBytes(body));
        });
        return pw.ToArray();
    }

    private static byte[] Fixed(string s)
    {
        var b = new byte[42];
        Encoding.Unicode.GetBytes(s).CopyTo(b, 0);
        return b;
    }

    /// <summary>1394 ReqMailList: [u16 tag][owner u32][npc handle u32@8].</summary>
    public static byte[] List(uint npcHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p => { p.WriteUInt16(0xE352); p.WriteUInt32(SelfHandle); p.WriteUInt32(npcHandle); });
        return pw.ToArray();
    }

    /// <summary>A GameDataStore holding only the given ItemInfo and Config_Def rows.</summary>
    public static async Task<GameDataStore> StoreAsync(ItemData[]? items = null, params (ushort Type, string Value)[] config)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-b9s1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), JsonSerializer.Serialize(items ?? []));
            await File.WriteAllTextAsync(Path.Combine(dir, "Config_Def.json"),
                JsonSerializer.Serialize(config.Select(c => new RO2.GameData.Tables.ConfigDef { type = c.Type, Value = c.Value })));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>An NpcStateManager that knows one mailbox NPC (there is no public way to place one).</summary>
    public static NpcStateManager NpcWithMailbox(uint handle, uint mapId = 1100)
    {
        var npcs = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance);
        var dict = (ConcurrentDictionary<uint, NpcRuntimeState>)typeof(NpcStateManager)
            .GetField("_npcs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(npcs)!;
        dict[handle] = new NpcRuntimeState { EntityOid = handle, MapId = mapId, PosX = 10, PosY = 10 };
        return npcs;
    }

    /// <summary>The blob of a compact-BA S2C body.</summary>
    public static byte[] Blob(byte[] body)
    {
        Assert.True(MailPackets.TryGetBlob(body, out var blob));
        return blob.ToArray();
    }
}

/// <summary>A DispatchProxy that throws from one named method of <typeparamref name="T"/> (before calling it) and forwards the rest.</summary>
internal class ThrowingProxy<T> : DispatchProxy where T : class
{
    private T _inner = null!;
    private string _method = "";

    public static T Wrap(T inner, string method)
    {
        var proxy = Create<T, ThrowingProxy<T>>();
        var self = (ThrowingProxy<T>)(object)proxy;
        self._inner = inner;
        self._method = method;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == _method) throw new IOException("injected " + _method + " failure");
        try { return targetMethod.Invoke(_inner, args); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }
}
