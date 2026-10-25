using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Keeps the close-range chat room signs in step with the chat server. It listens for room signals and notifies the players on the owner's map when a room opens or closes, when a member joins or leaves, and when ownership passes to another member.
/// </summary>
/// <remarks>
/// <para>
/// Owner exit: the room sign is removed for players on the owner's map.
/// </para>
/// <para>
/// Join and member exit: the joining or leaving member is announced, and the member count on the owner's sign is updated.
/// </para>
/// <para>
/// Master hand-off: when the owner leaves a room that still has members, the sign is removed from the old owner and placed on the new owner with the room's name, password flag and capacity.
/// </para>
/// <para>
/// Recipients are the players on the owner's map, plus the joining or leaving member when that member is on another map.
/// </para>
/// </remarks>
public sealed class CloseRangeExitListener(ICloseRangeRoomSignals signals, PlayerStateManager players,
    BroadcastService broadcast, ILogger<CloseRangeExitListener> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        
        var startedUtc = DateTime.UtcNow;
        logger.LogInformation("Close-range room signal listener started");
        return signals.SubscribeAsync(startedUtc, HandleAsync,
            ex => logger.LogWarning(ex, "Close-range room signal tail failed; retrying"), stoppingToken);
    }

    private Task HandleAsync(CloseRangeRoomSignal signal)
    {
        if (signal.Kind == CloseRangeRoomSignals.JoinKind)
            return JoinAsync(signal);
        
        if (signal.Kind != CloseRangeRoomSignals.ExitKind)
            return Task.CompletedTask;

        
        uint newMasterHandle = signal.NewMasterCharacterId != 0
            && players.GetPlayerByCharacterId(signal.NewMasterCharacterId) is { } heir ? heir.EntityId : 0;
        return CloseRangeChatRooms.ResolveExit(signal.RoomGuid, signal.CharacterId, signal.NewMasterCharacterId,
                newMasterHandle) switch
        {
            CloseRangeOwnerExited owner => RetractSignAsync(owner.Room),
            CloseRangeMemberExited member => MemberExitAsync(member, signal.MemberCount),
            CloseRangeMasterHandedOff handOff => HandOffAsync(handOff, signal.OwnerGone, signal.MemberCount),
            CloseRangeUnknownRoom unknown => LogUnknownRoom(unknown, signal.CharacterId),
        };
    }

 /// <summary>
 /// Notifies the players about an owner hand-off: the old owner's sign is removed unless the old owner has already disconnected, then the room sign and member count are sent to the new owner's map.
 /// </summary>
    private async Task HandOffAsync(CloseRangeMasterHandedOff handOff, bool ownerGone, int memberCount)
    {
        var (oldRoom, room) = (handOff.OldRoom, handOff.NewRoom);
        var oldMaster = ownerGone ? null : OwnerWithSign(oldRoom);
        var newMaster = OwnerWithSign(room);
        var anchor = newMaster ?? oldMaster;
        if (anchor is null)
        {
            logger.LogDebug("Close-range room {Room} passed from {Old} to {New}; neither is on this Zone with a sign to show",
                room.Id, oldRoom.OwnerCharacterId, room.OwnerCharacterId);
            return;
        }

        
        if (oldMaster is not null)
            await SendAroundAsync(anchor, oldMaster, ZoneS2COpcodes.NfyExitCloseRangeChatRoom,
                CloseRangeChatPackets.BuildNfyExitCloseRangeChatRoom2765(oldRoom.OwnerHandle));

        if (newMaster is null)
        {
            logger.LogInformation("Close-range room {Room} passed from {Old} to {New}, who is not on this Zone; no 2766",
                room.Id, oldRoom.OwnerCharacterId, room.OwnerCharacterId);
            return;
        }
        if (room.NameBuffer is not { } name)
        {
            logger.LogWarning("Close-range room {Room} has no stored name; its hand-off to {New} cannot be shown (2766)",
                room.Id, newMaster.Name);
            return;
        }
        await SendAroundAsync(newMaster, null, ZoneS2COpcodes.NfyNewMasterCloseRangeChatRoom,
            CloseRangeChatPackets.BuildNfyNewMasterCloseRangeChatRoom2766(room.OwnerHandle, name, room.HasPassword,
                room.Id.ToByteArray()));
        
        await SendAroundAsync(newMaster, null, ZoneS2COpcodes.NfyUpdateJoinMemberCountCloseRangeChatRoom,
            CloseRangeChatPackets.BuildNfyUpdateJoinMemberCount2770(room.OwnerHandle, room.Capacity, CountByte(memberCount)));

        logger.LogInformation("Close-range room {Room} passed from {Old} to {Name} ({Count} member(s)){Gone}; 2766 + 2770 sent",
            room.Id, oldRoom.OwnerCharacterId, newMaster.Name, memberCount, ownerGone ? ", the old master disconnected" : "");
    }

    private async Task RetractSignAsync(CloseRangeRoom room)
    {
        var owner = OwnerWithSign(room);
        if (owner is null)
        {
            logger.LogDebug("Close-range room {Room} closed; its owner {Char} is no longer on this Zone with that sign",
                room.Id, room.OwnerCharacterId);
            return;
        }

        var body = CloseRangeChatPackets.BuildNfyExitCloseRangeChatRoom2765(room.OwnerHandle);
        if (broadcast.GetSession(owner.SessionId) is { } session)
            await session.SendRmiAsync(ZoneS2COpcodes.NfyExitCloseRangeChatRoom, body);
        await broadcast.BroadcastToNearbyAsync(owner, ZoneS2COpcodes.NfyExitCloseRangeChatRoom,
            writer => writer.WriteBytes(body));

        logger.LogInformation("{Name} closed close-range chat room {Room}; sign taken down (2765)", owner.Name, room.Id);
    }

    private async Task JoinAsync(CloseRangeRoomSignal signal)
    {
        var room = CloseRangeChatRooms.Find(signal.RoomGuid);
        var joiner = players.GetPlayerByCharacterId(signal.CharacterId);
        if (room is null || joiner is null)
        {
            logger.LogDebug("Character {Char} joined close-range room {Room}, which this Zone {Why}", signal.CharacterId,
                Convert.ToHexString(signal.RoomGuid), room is null ? "did not open" : "cannot place (joiner not here)");
            return;
        }

        var anchor = OwnerWithSign(room) ?? joiner;
        await SendAroundAsync(anchor, joiner, ZoneS2COpcodes.NfyJoinCloseRangeChatRoom,
            CloseRangeChatPackets.BuildNfyJoinCloseRangeChatRoom2764(joiner.EntityId));
        await SendAroundAsync(anchor, joiner, ZoneS2COpcodes.NfyUpdateJoinMemberCountCloseRangeChatRoom,
            CloseRangeChatPackets.BuildNfyUpdateJoinMemberCount2770(room.OwnerHandle, room.Capacity, CountByte(signal.MemberCount)));

        logger.LogInformation("{Name} joined close-range chat room {Room} ({Count} member(s)); 2764 + 2770 sent",
            joiner.Name, room.Id, signal.MemberCount);
    }

    private async Task MemberExitAsync(CloseRangeMemberExited member, int memberCount)
    {
        var room = member.Room;
        var leaver = players.GetPlayerByCharacterId(member.CharacterId);
        var anchor = OwnerWithSign(room) ?? leaver;
        if (anchor is null)
        {
            logger.LogDebug("Character {Char} left close-range room {Room}; neither it nor the owner is on this Zone",
                member.CharacterId, room.Id);
            return;
        }

        
        if (leaver is not null)
            await SendAroundAsync(anchor, leaver, ZoneS2COpcodes.NfyExitCloseRangeChatRoom,
                CloseRangeChatPackets.BuildNfyExitCloseRangeChatRoom2765(leaver.EntityId));

        
        if (memberCount > 0)
            await SendAroundAsync(anchor, leaver, ZoneS2COpcodes.NfyUpdateJoinMemberCountCloseRangeChatRoom,
                CloseRangeChatPackets.BuildNfyUpdateJoinMemberCount2770(room.OwnerHandle, room.Capacity, CountByte(memberCount)));

        logger.LogInformation("Character {Char} left close-range chat room {Room} ({Count} member(s) left)",
            member.CharacterId, room.Id, memberCount);
    }

    private Task LogUnknownRoom(CloseRangeUnknownRoom unknown, uint characterId)
    {
        logger.LogDebug("Character {Char} left close-range room {Room}, which this Zone did not open", characterId, unknown.Id);
        return Task.CompletedTask;
    }

 /// <summary>Returns the room's owner when that owner is still on this Zone with the handle its sign is attached to; otherwise null.</summary>
    private PlayerState? OwnerWithSign(CloseRangeRoom room)
        => players.GetPlayerByCharacterId(room.OwnerCharacterId) is { } owner && owner.EntityId == room.OwnerHandle
            ? owner
            : null;

 /// <summary>
 /// Sends <paramref name="body"/> to every player on <paramref name="anchor"/>'s map, and to <paramref name="member"/> when that player is on another map.
 /// </summary>
    private async Task SendAroundAsync(PlayerState anchor, PlayerState? member, ushort opcode, byte[] body)
    {
        await broadcast.BroadcastToMapAsync(anchor.MapId, opcode, writer => writer.WriteBytes(body));
        if (member is not null && member.MapId != anchor.MapId && broadcast.GetSession(member.SessionId) is { } session)
            await session.SendRmiAsync(opcode, body);
    }

 /// <summary>Converts a member count to the single byte the client reads, clamped to the valid range.</summary>
    private static byte CountByte(int memberCount) => (byte)Math.Clamp(memberCount, 0, byte.MaxValue);
}
