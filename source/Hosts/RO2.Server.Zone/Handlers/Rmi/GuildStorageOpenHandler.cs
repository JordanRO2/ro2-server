using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1489 ReqOpenGuildStorage: opens the guild vault at a vault NPC.
/// </summary>
/// <remarks>
/// <para>
/// The request body is 20 bytes: <c>[16-byte guild GUID][u32 NPC handle]</c>. The NPC handle must resolve to a live NPC on the player's map; otherwise the server answers with result code 55 (out of range) and the window is not opened.
/// </para>
/// <para>
/// Every accepted open is answered with AnsOpenGuildStorage (2674) carrying the guild's stored items and funds, regardless of how many items the vault holds.
/// </para>
/// </remarks>
public class GuildStorageOpenHandler : ZoneRmiHandlerBase<GuildStorageOpenHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqOpenGuildStorage;
    public override string Name => "GuildStorageOpen";
    public GuildStorageOpenHandler(IServiceScopeFactory s, ILogger<GuildStorageOpenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Expected length of the 1489 request body, in bytes.
 /// </summary>
    internal const int RequestLength = 20;

 /// <summary>
 /// Offset of the u32 NPC handle in the 1489 body, after the 16-byte guild GUID.
 /// </summary>
    internal const int NpcHandleOffset = 16;

 /// <summary>
 /// Result code 55 (out of range). The client does not open the window for this code.
 /// </summary>
    internal const ushort ResultOutOfRange = 55;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength)
        {
            Logger.LogWarning("Session {Id}: {Name} sent a {Len}-byte ReqOpenGuildStorage, need {Need}",
                session.SessionId, player.Name, payload.Length, RequestLength);
            return;
        }

        var npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(NpcHandleOffset, 4));
        var npc = services.GetRequiredService<NpcStateManager>().GetNpc(npcHandle);
        if (npc is null || npc.IsDead || npc.MapId != player.MapId)
        {
            Logger.LogInformation("Session {Id}: {Name} opens guild storage at {Handle:X8}, which is no live NPC on map {Map}: code {Code}",
                session.SessionId, player.Name, npcHandle, player.MapId, ResultOutOfRange);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsOpenGuildStorage, (ReadOnlyMemory<byte>)BuildOutOfRangeRefusal2674());
            return;
        }

        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        
        
        if (guild != null) GuildVaultWindows.Open(session.SessionId, player.CharacterId, PlayerManager.GetPlayer);
        var body = guild != null
            ? GuildPackets.BuildGuildStorageList(guild)
            : GuildPackets.BuildGuildStorageList(new Guild());   
        Logger.LogDebug("{Name} opens guild storage at NPC {Npc} ({Count} items)", player.Name, npc.NpcId, guild?.Storage.Count ?? 0);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsOpenGuildStorage, (ReadOnlyMemory<byte>)body);
    }

 /// <summary>
 /// Builds the out-of-range answer: the list layout with result 55, no funds and no items (16 bytes).
 /// </summary>
    internal static byte[] BuildOutOfRangeRefusal2674()
    {
        using var w = new PacketWriter(16);
        w.WriteUInt16(ResultOutOfRange);
        w.WriteUInt32(10);   
        w.WriteUInt64(0);
        w.WriteUInt16(0);
        return w.GetWrittenSpan().ToArray();
    }
}

/// <summary>
/// Tracks the sessions whose guild vault window is open, so vault change notifications reach the right members.
/// </summary>
/// <remarks>
/// <para>
/// A window opens on an answered open (1489) and closes on 1490 ReqCloseGuildStorage or when the session ends. Vault changes (2675 NfyAddGuildStorage_Item, 2676 NfyDelGuildStorage_Item) go to the member who made the change and to every other member with the window open. Guild members without the window open receive nothing.
/// </para>
/// <para>
/// A stale entry is harmless: a notification sent to a window that was closed without 1490 only edits a list that the next open replaces.
/// </para>
/// </remarks>
public static class GuildVaultWindows
{
 /// <summary>
 /// Session id to the character that opened the window in that session.
 /// </summary>
    private static readonly ConcurrentDictionary<uint, uint> OpenBySession = new();

 /// <summary>
 /// Marks the window open in this session. Also drops entries of sessions that have ended.
 /// </summary>
    public static void Open(uint sessionId, uint characterId, Func<uint, PlayerState?> playerBySession)
    {
        OpenBySession[sessionId] = characterId;
        foreach (var (sid, cid) in OpenBySession)
            if (!IsLive(sid, cid, playerBySession)) OpenBySession.TryRemove(new KeyValuePair<uint, uint>(sid, cid));
    }

 /// <summary>
 /// Marks the window closed in this session (1490 ReqCloseGuildStorage).
 /// </summary>
    public static void Close(uint sessionId) => OpenBySession.TryRemove(sessionId, out _);

 /// <summary>
 /// Returns the sessions with an open vault window whose character is a member of <paramref name="guild"/>, excluding <paramref name="exceptSessionId"/>.
 /// Entries whose session has ended, or now holds another character, are removed.
 /// </summary>
    public static List<uint> OtherViewers(Guild guild, uint exceptSessionId, Func<uint, PlayerState?> playerBySession)
    {
        HashSet<uint> members;
        lock (guild.Gate) members = guild.Members.Select(m => m.CharId).ToHashSet();
        var viewers = new List<uint>();
        foreach (var (sid, cid) in OpenBySession)
        {
            if (!IsLive(sid, cid, playerBySession))
            {
                OpenBySession.TryRemove(new KeyValuePair<uint, uint>(sid, cid));
                continue;
            }
            if (sid != exceptSessionId && members.Contains(cid)) viewers.Add(sid);
        }
        viewers.Sort();
        return viewers;
    }

 /// <summary>
 /// Sends one built 2675 or 2676 body to every session returned by <see cref="OtherViewers"/>.
 /// </summary>
    public static async Task SendToOtherViewersAsync(Guild guild, uint exceptSessionId, PlayerStateManager pm,
        BroadcastService bc, ushort opcode, byte[] body)
    {
        foreach (var sid in OtherViewers(guild, exceptSessionId, pm.GetPlayer))
        {
            var viewer = bc.GetSession(sid);
            if (viewer == null) continue;
            await viewer.SendRmiAsync(opcode, (ReadOnlyMemory<byte>)body);
        }
    }

    private static bool IsLive(uint sessionId, uint characterId, Func<uint, PlayerState?> playerBySession)
        => playerBySession(sessionId) is { } p && p.CharacterId == characterId;
}
