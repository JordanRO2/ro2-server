using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Configuration;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1315 (chat line) and its fan-out as S2C RMI 2324 OnReceiveChat, built by <see cref="ChatPackets"/>.
/// </summary>
/// <remarks>
/// <para>
/// Request body: <c>[u32 blobLen][u16 innerSize 0][u16 msgTag][u32 sender handle][u8 channel][u16 msgLen][msgLen UTF-16 units]</c>.
/// The channel is one of the <see cref="ChatPackets.ChannelNormal"/> family of values.
/// </para>
/// <para>
/// Audience by channel:
/// <list type="bullet">
/// <item>Normal and Area: the players on the sender's map, including the sender.</item>
/// <item>Party and Raid: the sender's party. In a raid, the Party channel is limited to the sender's frame group.</item>
/// <item>Guild: the sender's guild, on any map.</item>
/// </list>
/// A line on the Party or Guild channel from a player with no party or guild is dropped. Any other channel value is
/// dropped as well, so a modified client cannot post as a system or dialog channel to a whole map.
/// </para>
/// </remarks>
public class ChatHandler : ZoneRmiHandlerBase<ChatHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCChatting;
    public override string Name => "Chat";

 /// <summary>
 /// PartyType value of a raid party. Every roster with more than five members is a raid.
 /// </summary>
    private const byte RaidPartyType = 2;

 /// <summary>Members per raid frame group: the raid view groups the 20 frame slots by (slot-1)/5.</summary>
    private const int RaidGroupSize = 5;

 /// <summary>Storage type of the inventory bag; worn gear and bank storage use other values.</summary>
    private const byte BagStorageType = 0;

 /// <summary>Offset of the u8 stack count inside a 126-byte item record. 0 empties the client's bag cell.</summary>
    private const int ItemRecordCountOffset = 14;

 /// <summary>Slot value that asks the inventory to assign a free bag cell.</summary>
    private const int AutoAssignSlot = -1;

    public ChatHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<ChatHandler> logger,
        PlayerStateManager playerManager,
        BroadcastService broadcast)
        : base(scopeFactory, logger, playerManager, broadcast)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        if (!TryReadReqPCChatting(payload.Span, out var channel, out var msgBytes))
            return;

        
        
        var message = Encoding.Unicode.GetString(msgBytes).TrimEnd('\0');
        if (message.Length > 0 && char.IsControl(message[^1])) message = message[..^1];

        
        
        
        
        
        if (message.StartsWith('/'))
        {
            await HandleCommandAsync(session, player, message, services);
            return;
        }

        Logger.LogInformation("[Chat] [{Channel}] {Name}: {Message}",
            channel, player.Name, message);

        await RelayAsync(session, player, channel, msgBytes, services);
    }

 /// <summary>
 /// Reads the 1315 body: the channel at body offset 12, and the message units after the u16 length at offset 13.
 /// The message is kept as the client's bytes, since the 2324 echoes them verbatim. Returns false for a body that
 /// is too short for its length or holds an empty message.
 /// </summary>
    internal static bool TryReadReqPCChatting(ReadOnlySpan<byte> body, out byte channel, out byte[] msgUnits)
    {
        const int ChannelOff = 12;  // after [u32 blobLen][u16 innerSize][u16 msgTag][u32 sender handle]
        const int MsgLenOff = 13;
        const int MsgOff = 15;
        channel = 0;
        msgUnits = Array.Empty<byte>();
        if (body.Length < MsgOff)
            return false;
        channel = body[ChannelOff];
        int msgLen = BinaryPrimitives.ReadUInt16LittleEndian(body[MsgLenOff..]);
        if (msgLen == 0 || body.Length - MsgOff < msgLen * 2)
            return false;
        msgUnits = body.Slice(MsgOff, msgLen * 2).ToArray();
        return true;
    }

 /// <summary>
 /// Sends the line to the channel's audience (see the class summary), including the sender. One body is built per
 /// line and sent to every recipient.
 /// </summary>
    private async Task RelayAsync(ProudSession session, PlayerState player, byte channel, byte[] msgBytes,
        IServiceProvider services)
    {
        var nameUnits = Encoding.Unicode.GetBytes(player.Name);
        switch (channel)
        {
            case ChatPackets.ChannelNormal:
            case ChatPackets.ChannelArea:
            {
                var body = ChatPackets.BuildOnReceiveChat2324(player.EntityId, nameUnits, channel, msgBytes,
                    innerSizeIsLength: false);
                await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnReceiveChat,
                    writer => writer.WriteBytes(body));
                return;
            }
            case ChatPackets.ChannelParty:
            case ChatPackets.ChannelRaid:
            {
                var parties = services.GetRequiredService<PartyService>();
                var party = parties.GetPlayerParty(session.SessionId);
                if (party == null)
                {
                    Logger.LogInformation("[Chat] {Name} spoke on party channel {Channel} with no party; dropped",
                        player.Name, channel);
                    return;
                }
                var body = ChatPackets.BuildOnReceiveChat2324(PartyAnswerPackets.PartyHandle(party), nameUnits,
                    channel, msgBytes, innerSizeIsLength: false);
                foreach (var memberKey in PartyAudience(party, session.SessionId, channel,
                             key => parties.GetOnlineMember(party, key) != null))
                {
                    if (Broadcast.GetSession(memberKey) is { } memberSession)
                        await memberSession.SendInternalRmiAsync(ZoneS2COpcodes.OnReceiveChat, body);
                }
                return;
            }
            case ChatPackets.ChannelGuild:
            {
                var guild = GuildService.Instance.GetByCharId(player.CharacterId);
                if (guild == null)
                {
                    Logger.LogInformation("[Chat] {Name} spoke on the guild channel with no guild; dropped", player.Name);
                    return;
                }
                var body = ChatPackets.BuildOnReceiveChat2324(player.EntityId, nameUnits, channel, msgBytes,
                    innerSizeIsLength: false);
                foreach (var charId in GuildAudience(guild))
                {
                    if (PlayerManager.GetPlayerByCharacterId(charId) is { } member
                        && Broadcast.GetSession(member.SessionId) is { } memberSession)
                        await memberSession.SendInternalRmiAsync(ZoneS2COpcodes.OnReceiveChat, body);
                }
                return;
            }
            default:
                Logger.LogWarning("[Chat] {Name} sent chat channel {Channel}, which no client sends; dropped",
                    player.Name, channel);
                return;
        }
    }

 /// <summary>
 /// Returns the member keys of <paramref name="party"/> that receive a line on <paramref name="channel"/> from
 /// <paramref name="senderKey"/>: the online members (<paramref name="isOnline"/>), and on a raid's Party channel
 /// only the members of the sender's frame group. The sender is always included.
 /// </summary>
    internal static List<uint> PartyAudience(PartyState party, uint senderKey, byte channel, Func<uint, bool> isOnline)
    {
        bool senderGroupOnly = channel == ChatPackets.ChannelParty && party.PartyType == RaidPartyType;
        int senderGroup = FrameGroup(PartyService.GetFrameSlot(party, senderKey));
        var audience = new List<uint>();
        foreach (var memberKey in party.MemberSessionIds.ToArray())
        {
            if (!isOnline(memberKey)) continue;
            if (senderGroupOnly && FrameGroup(PartyService.GetFrameSlot(party, memberKey)) != senderGroup) continue;
            audience.Add(memberKey);
        }
        return audience;
    }

 /// <summary>The raid frame group of a 1-based frame slot; -1 for a member that holds no slot.</summary>
    internal static int FrameGroup(byte slot) => slot == 0 ? -1 : (slot - 1) / RaidGroupSize;

 /// <summary>The character ids of <paramref name="guild"/>'s members, the sender among them.</summary>
    internal static uint[] GuildAudience(Guild guild)
    {
        lock (guild.Gate) return guild.Members.Select(m => m.CharId).ToArray();
    }

    
 /// <summary>
 /// Minimum account <see cref="PlayerState.AccessLevel"/> that unlocks the mutating GM commands (/i, /tp, /speed).
 /// 0 means every player. The level is account-wide.
 /// </summary>
    private const byte GmAccessLevel = 1;

 /// <summary>
 /// Returns true when the player's account is a GM (<see cref="GmAccessLevel"/> or higher). Otherwise tells the
 /// sender that access is denied, logs the denial and returns false so the caller stops.
 /// </summary>
    private async Task<bool> EnsureGmAsync(ProudSession session, PlayerState player)
    {
        if (player.AccessLevel >= GmAccessLevel) return true;
        
        await SendGmAnswerAsync(session, GmCommandPackets.GmGrade,
            player.AccessLevel.ToString(CultureInfo.InvariantCulture));
        Logger.LogWarning("[Cmd] {Name} (account {Acct}, accessLevel {Lvl}) denied a GM command",
            player.Name, player.AccountId, player.AccessLevel);
        return false;
    }

 /// <summary>
 /// Whether a slash line from <paramref name="player"/> that names no GM command, or names <c>/pos</c>, gets an
 /// answer. Only GMs get one; other players receive no reply to an unknown slash line.
 /// </summary>
    internal static bool AnswersSlashLine(PlayerState player) => player.AccessLevel >= GmAccessLevel;

    private async Task HandleCommandAsync(ProudSession session, PlayerState player, string message, IServiceProvider services)
    {
        var parts = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/pos" when !AnswersSlashLine(player):
                
                
                Logger.LogInformation("[Cmd] {Name} /pos dropped: not a GM (not relayed, no answer)", player.Name);
                break;
            case "/pos":
                Logger.LogInformation("[Cmd] {Name} /pos -> map {Map} ({X:F1},{Y:F1},{Z:F1})",
                    player.Name, player.MapId, player.X, player.Y, player.Z);
                
                
                await SendGmAnswerAsync(session, GmCommandPackets.PcLocation, string.Create(CultureInfo.InvariantCulture,
                    $"{player.MapId} {player.X:F1},{player.Y:F1},{player.Z:F1}"));
                break;
            case "/tp":
                if (!await EnsureGmAsync(session, player)) break;
                await HandleTpAsync(session, player, parts, services);
                break;
            case "/speed":
                if (!await EnsureGmAsync(session, player)) break;
                await HandleSpeedAsync(session, player, parts);
                break;
            case "/i":
            case "/item":
                if (!await EnsureGmAsync(session, player)) break;
                await HandleGiveItemAsync(session, player, parts, services);
                break;
            case "/zeny":
            case "/money":
                if (!await EnsureGmAsync(session, player)) break;
                await HandleGiveZenyAsync(session, player, parts, services);
                break;
            case "/clear":
                if (!await EnsureGmAsync(session, player)) break;
                await HandleClearBagAsync(session, player, services);
                break;
            case AnnouncementService.NoticeCommand:
                if (!await EnsureGmAsync(session, player)) break;
                if (!AnnouncementService.TryParseNoticeCommand(message, out var noticeRgb, out var noticeText))
                { await SendSystemAsync(session, AnnouncementService.NoticeUsage); break; }
                await services.GetRequiredService<AnnouncementService>().BroadcastNoticeAsync(noticeText, noticeRgb);
                Logger.LogInformation("[Cmd] {Name} /notice #{Rgb:X6} {Text}", player.Name, noticeRgb, noticeText);
                break;
            default:
                if (!AnswersSlashLine(player))
                {
                    
                    Logger.LogInformation("[Cmd] {Name} unknown slash line dropped (not relayed, no answer)", player.Name);
                    break;
                }
                await SendSystemAsync(session, "cmds: /pos | /tp <mapId> <x>,<y>,<z> | /tp <x>,<y>,<z> | /speed <n> | /i <itemId> [qty] [player] | /zeny [amount] | /clear | /notice <RRGGBB> <text>");
                break;
        }
    }

 /// <summary>
 /// /zeny [amount]: grants the caller zeny. The argument is the internal gold value, which the client's wallet
 /// shows divided down. Defaults to 10,000,000. Adds to the balance, persists it, and pushes the wallet update
 /// (2435 OnZenyChange).
 /// </summary>
    private async Task HandleGiveZenyAsync(ProudSession session, PlayerState player, string[] parts, IServiceProvider services)
    {
        long amount = services.GetRequiredService<ServerConfiguration>().Game.GmDefaultZenyAmount;
        if (parts.Length >= 2 &&
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out amount))
        { await SendSystemAsync(session, "usage: /zeny [amount]   (default 10000000)"); return; }
        if (amount <= 0) { await SendSystemAsync(session, "amount must be a positive integer"); return; }

        
        player.Credit(amount);                       
        long newGold = player.Gold;
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, newGold);

        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnZenyChange,
            ActorVitalsPackets.BuildZenyChange2435(player.EntityId, newGold));

        Logger.LogInformation("[Cmd] {Name} /zeny +{Amount} -> {Total}", player.Name, amount, newGold);
        await SendSystemAsync(session, $"Granted {amount:N0} zeny. Balance: {newGold:N0}");
    }

 /// <summary>/tp &lt;mapId&gt; &lt;x&gt;,&lt;y&gt;,&lt;z&gt; (or /tp &lt;x&gt;,&lt;y&gt;,&lt;z&gt; for the current map).</summary>
    private async Task HandleTpAsync(ProudSession session, PlayerState player, string[] parts, IServiceProvider services)
    {
        uint mapId = player.MapId;
        string coords;
        if (parts.Length >= 3 && uint.TryParse(parts[1], out var mid)) { mapId = mid; coords = string.Join("", parts[2..]); }
        else if (parts.Length >= 2) { coords = string.Join("", parts[1..]); }
        else { await SendSystemAsync(session, "usage: /tp <mapId> <x>,<y>,<z>  or  /tp <x>,<y>,<z>"); return; }

        var c = coords.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (c.Length < 3
            || !float.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        { await SendGmAnswerAsync(session, GmCommandPackets.InvalidCoordinates, coords); return; }

        var store = services.GetRequiredService<GameDataStore>();
        if (store.GetMap(mapId) == null)
        {
            await SendGmAnswerAsync(session, GmCommandPackets.InvalidMapId, mapId.ToString(CultureInfo.InvariantCulture));
            return;
        }

        
        var characters = services.GetRequiredService<ICharacterRepository>();
        await characters.UpdatePositionAsync(player.Character?.Id ?? player.CharacterId, mapId, x, y, z);

        
        await TeleportLiveAsync(session, player, mapId, x, y, z, services);

        Logger.LogInformation("[Cmd] {Name} /tp -> map {Map} ({X:F1},{Y:F1},{Z:F1})",
            player.Name, mapId, x, y, z);
        await SendSystemAsync(session, $"tp -> map {mapId} ({x:F1}, {y:F1}, {z:F1})");
    }

 /// <summary>
 /// Relocates the actor within the current session without a relog. Sends the map-change sequence the server uses
 /// for a teleport: despawn from the old map (2505, 2507), spawn of the destination NPCs (2506), the map load
 /// (2311), then the quick-slot, vehicle and vehicle-list updates (2472, 2523, 2522).
 /// Login-only packets (2310, 2558) are not sent, because they cannot be replayed on a live actor. The move speed
 /// is re-sent when the client acknowledges the map load.
 /// </summary>
    
    
    private static Task TeleportLiveAsync(ProudSession session, PlayerState player, uint mapId, float x, float y, float z, IServiceProvider services)
        => WarpService.WarpAsync(session, player, mapId, x, y, z, services);

 /// <summary>/speed &lt;value&gt;: sets the actor's move speed (2497 OnChangeActorMoveSpeed; default 5).</summary>
    private async Task HandleSpeedAsync(ProudSession session, PlayerState player, string[] parts)
    {
        if (parts.Length < 2) { await SendSystemAsync(session, "usage: /speed <value>  (default 5)"); return; }
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        { await SendGmAnswerAsync(session, GmCommandPackets.IncorrectSpeed, parts[1]); return; }

        
        
        
        
        
        
        
        if (!float.IsFinite(speed) || speed <= 0f)
        { await SendGmAnswerAsync(session, GmCommandPackets.IncorrectSpeed, parts[1]); return; }

        await session.SendInternalRmiAsync(
            ZoneS2COpcodes.OnChangeActorMoveSpeed, TraitPackets.BuildActorMoveSpeed2497(player.EntityId, speed));

        Logger.LogInformation("[Cmd] {Name} /speed {Speed}", player.Name, speed);
        await SendSystemAsync(session, $"speed -> {speed}");
    }

 /// <summary>
 /// <c>/i &lt;itemId&gt;[,&lt;qty&gt;[,&lt;player&gt;]]</c>: puts an item in a bag.
 /// <list type="bullet">
 /// <item><c>/i 100</c>: one item to the sender.</item>
 /// <item><c>/i 100,5</c>: five items to the sender.</item>
 /// <item><c>/i 100,5,Bob</c>: five items to the online player Bob.</item>
 /// </list>
 /// Arguments may be separated by commas or spaces. The recipient name may contain spaces.
 /// </summary>
    private async Task HandleGiveItemAsync(ProudSession session, PlayerState player, string[] parts, IServiceProvider services)
    {
        const string usage = "usage: /i <itemId> [qty] [player]   (commas or spaces — /i 100 5 Bob  or  /i 100,5,Bob)";
        if (parts.Length < 2) { await SendSystemAsync(session, usage); return; }

        
        
        
        var tokens = string.Join(' ', parts[1..])
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) { await SendSystemAsync(session, usage); return; }

        if (!uint.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId) || itemId == 0)
        { await SendSystemAsync(session, $"bad item id — {usage}"); return; }

        
        
        int qty = 1;
        int nameStart = 1;
        if (tokens.Length >= 2 &&
            int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var q))
        {
            if (q < 1) { await SendSystemAsync(session, "quantity must be a positive integer"); return; }
            qty = q;
            nameStart = 2;
        }
        var maxGiveQuantity = services.GetRequiredService<ServerConfiguration>().Game.GmMaxGiveQuantity;
        if (qty > maxGiveQuantity) qty = maxGiveQuantity;

        
        var target = player;
        var targetSession = session;
        if (tokens.Length > nameStart)
        {
            var name = string.Join(' ', tokens[nameStart..]);
            var found = PlayerManager.GetPlayerByName(name);
            var foundSession = found != null ? Broadcast.GetSession(found.SessionId) : null;
            if (found == null || foundSession == null)
            { await SendGmAnswerAsync(session, GmCommandPackets.PcNotFoundByName, name); return; }
            target = found;
            targetSession = foundSession;
        }

        
        var store = services.GetRequiredService<GameDataStore>();
        var itemData = store.GetItem(itemId);
        if (itemData == null)
        {
            await SendGmAnswerAsync(session, GmCommandPackets.ItemNotFound, itemId.ToString(CultureInfo.InvariantCulture));
            return;
        }

        var given = await GiveItemStacksAsync(targetSession, target, itemId, qty, itemData.Stack_Max, services);

        Logger.LogInformation("[Cmd] {Gm} /i {Item} x{Qty} -> {Target}",
            player.Name, itemId, given, target.Name);

        var label = string.IsNullOrEmpty(itemData.Name) ? itemId.ToString() : $"{itemData.Name} ({itemId})";
        if (target.SessionId == player.SessionId)
        {
            await SendSystemAsync(session, $"gave you {given}x {label}");
        }
        else
        {
            await SendSystemAsync(session, $"gave {given}x {label} to {target.Name}");
            await SendSystemAsync(targetSession, $"received {given}x {label} from {player.Name}");
        }
    }

 /// <summary>
 /// Adds <paramref name="qty"/> of <paramref name="itemId"/> to the target's bag, in stacks of the item's maximum
 /// stack size. Each stack is persisted and sent as a pickup update, so the item appears live. Returns the amount
 /// granted, which is capped per command.
 /// </summary>
    private async Task<int> GiveItemStacksAsync(
        ProudSession targetSession, PlayerState target, uint itemId, int qty, byte stackMax, IServiceProvider services)
    {
        int perStack = stackMax < 1 ? 1 : stackMax;
                var inventory = services.GetService<IInventoryRepository>();

        var maxGiveRecords = services.GetRequiredService<ServerConfiguration>().Game.GmMaxGiveRecords;
        int remaining = qty, granted = 0, records = 0;
        while (remaining > 0 && records < maxGiveRecords)
        {
            int count = Math.Min(perStack, remaining);
            var newItem = new InventoryItem
            {
                CharacterId = target.CharacterId,
                ItemId = itemId,
                Count = count,
                Slot = AutoAssignSlot,
                StorageType = BagStorageType,
            };
            if (inventory != null)
            {
                try { await inventory.AddItemAsync(newItem); } 
                catch {   }
            }

            var body = LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(newItem));
            await targetSession.SendRmiAsync(ZoneS2COpcodes.OnPickupItem, w => w.WriteBytes(body));

            remaining -= count;
            granted += count;
            records++;
        }
        return granted;
    }

 /// <summary>
 /// <c>/clear</c>: deletes every item in the sender's inventory bag. Worn equipment and bank storage are not
 /// affected. Each cleared cell is emptied live, then its row is removed from the database. GM only.
 /// </summary>
    private async Task HandleClearBagAsync(ProudSession session, PlayerState player, IServiceProvider services)
    {
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var all = await inventory.GetByCharacterAsync(player.CharacterId);

        int cleared = 0;
        foreach (var it in all)
        {
            if (it.StorageType != BagStorageType) continue;   

            
            var rec = WorldEntryService.BuildItemRecord126(it);
            rec[ItemRecordCountOffset] = 0;
            await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem,
                w => w.WriteBytes(QuestProgressPackets.BuildItemRemoved2379(player.EntityId, rec)));
            await inventory.RemoveItemAsync(it.Id);
            cleared++;
        }

        Logger.LogInformation("[Cmd] {Name} /clear -> removed {Count} bag item(s)", player.Name, cleared);
        await SendSystemAsync(session, cleared == 0 ? "your bag is already empty" : $"cleared {cleared} item(s) from your bag");
    }

 /// <summary>
 /// Answers a GM command with S2C 2893 AnsGmCommand. The client shows its localized string for
 /// <paramref name="code"/>, with <paramref name="argument"/> substituted, as a System chat line. Used for outcomes
 /// that have a matching string. Usage text and other outcomes use the English line from <see cref="SendSystemAsync"/>.
 /// </summary>
    private static Task SendGmAnswerAsync(ProudSession session, ushort code, string argument) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsGmCommand, GmCommandPackets.BuildAnsGmCommand2893(code, argument));

 /// <summary>
 /// Sends a one-off SYSTEM chat line to a single session (see <see cref="ChatPackets.BuildSystemLine2324"/>).
 /// </summary>
    private static Task SendSystemAsync(ProudSession session, string text)
        => session.SendInternalRmiAsync(ZoneS2COpcodes.OnReceiveChat, ChatPackets.BuildSystemLine2324(text));
}
