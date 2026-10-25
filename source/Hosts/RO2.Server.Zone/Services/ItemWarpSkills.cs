using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Item skills that move the player: Butterfly Wing, Dungeon Wing and the town warp scrolls.
/// </summary>
/// <remarks>
/// <para>
/// Town warps move the player when the cast's 1509 lands, after the 2718 acknowledgement. The Butterfly Wing and
/// Dungeon Wing move the player at the answer to the 1561 request. The form of the move follows the source map:
/// a channel hand-off from an instanced map, an in-place move onto the current map, or a regular departure.
/// </para>
/// </remarks>
internal static class ItemWarpSkills
{
 /// <summary>Butterfly Wing: returns to the bind point (<see cref="ButterflyWingDestination"/>).</summary>
    internal const uint ButterflyWing = 9100101;
 /// <summary>Dungeon Wing: leaves the current map for its Map_List return point (<see cref="DungeonWingDestination"/>).</summary>
    internal const uint DungeonWing = 9110001;
 /// <summary>Town warp scrolls with a fixed destination (<see cref="TownWarpDestination"/>).</summary>
    internal const uint PronteraWarp = 9100201, AlbertaWarp = 9100301, MorrocWarp = 9100701, TrainingGroundWarp = 9100801,
        JawaiiWarp = 9101001;

 /// <summary>Map_Type of an instanced dungeon map.</summary>
    internal const byte MapTypeInstance = 1;
 /// <summary>Map_Type of an open field or town map.</summary>
    internal const byte MapTypeField = 0;

 /// <summary>When the item skill's move takes effect.</summary>
    internal enum MoveKind
    {
 /// <summary>Not a moving item skill.</summary>
        None,
 /// <summary>Moves right after the request is answered (Butterfly Wing, Dungeon Wing).</summary>
        OnAnswer,
 /// <summary>Moves when the cast's landing (1509) arrives (town warps).</summary>
        OnLanding,
    }

    internal static MoveKind KindOf(uint skillId) => skillId switch
    {
        ButterflyWing or DungeonWing => MoveKind.OnAnswer,
        PronteraWarp or AlbertaWarp or MorrocWarp or TrainingGroundWarp or JawaiiWarp => MoveKind.OnLanding,
        _ => MoveKind.None,
    };

 /// <summary>Target position of a move.</summary>
    internal readonly record struct Destination(uint MapId, float X, float Y, float Z);

 /// <summary>
 /// Fixed destination of a town warp, or null for another skill or when the target map or Position_Info row is not loaded.
 /// </summary>
    internal static Destination? TownWarpDestination(uint skillId, Func<uint, MapData?> mapOf,
        IReadOnlyDictionary<uint, PositionData> positions)
    {
        Destination? dest = skillId switch
        {
            PronteraWarp => new Destination(1100, 798.12f, 767.1f, 57.18f),
            AlbertaWarp => new Destination(1200, 722.76f, 547.03f, 55.32f),
            MorrocWarp => new Destination(1300, 434f, 249f, 12f),
            TrainingGroundWarp => FromPosition(TrainingGroundPosition, positions),
            JawaiiWarp => FromPosition(JawaiiPosition, positions),
            _ => null,
        };
        return dest is { } d && mapOf(d.MapId) is not null ? d : null;
    }

 /// <summary>Position_Info rows of the Training Ground and Jawaii destinations (see <see cref="TownWarpDestination"/>).</summary>
    internal const uint TrainingGroundPosition = 294, JawaiiPosition = 410;

    private static Destination? FromPosition(uint positionId, IReadOnlyDictionary<uint, PositionData> positions)
        => positions.TryGetValue(positionId, out var p) ? new Destination(p.Area_ID, p.Position_X, p.Position_Y, p.Position_Z) : null;

 /// <summary>
 /// Destination of the Dungeon Wing: the current map's Map_List return point, or null when the wing is refused there.
 /// </summary>
 /// <remarks>
 /// The wing is refused on a Map_Type 0 map, on a map with Map_ID 0, and when the return map is not in Map_List.
 /// </remarks>
    internal static Destination? DungeonWingDestination(MapData? current, Func<uint, MapData?> mapOf)
    {
        if (current is null || current.Map_Type == MapTypeField || current.Map_ID == 0 || mapOf(current.Map_ID) is null)
            return null;
        return new Destination(current.Map_ID, current.x, current.y, current.z);
    }

 /// <summary>
 /// Destination of the Butterfly Wing: the bind point when its map is loaded, otherwise the new-character spawn.
 /// </summary>
 /// <remarks>
 /// Uses the save point and revival rules of TeleportSavePointHandler.ResolveSavePoint, excluding its first rule
 /// (the current map's return point).
 /// </remarks>
    internal static Destination ButterflyWingDestination(ushort bindIndex, Func<uint, MapData?> mapOf,
        IReadOnlyDictionary<uint, PositionData> positions)
    {
        if (bindIndex != 0 && positions.TryGetValue(bindIndex, out var bind) && mapOf(bind.Area_ID) is not null)
            return new Destination(bind.Area_ID, bind.Position_X, bind.Position_Y, bind.Position_Z);
        return new Destination(WorldEntryService.DefaultMapId, WorldEntryService.DefaultX, WorldEntryService.DefaultY,
            WorldEntryService.DefaultZ);
    }

 /// <summary>
 /// Destination of an <see cref="MoveKind.OnAnswer"/> skill from the player's current map, or null when it does not move.
 /// </summary>
    internal static Destination? AnswerDestination(uint skillId, PlayerState player, GameDataStore data) => skillId switch
    {
        ButterflyWing => ButterflyWingDestination(player.BindIndex, data.GetMap, data.Positions),
        DungeonWing => DungeonWingDestination(data.GetMap(player.MapId), data.GetMap),
        _ => null,
    };

    
 /// <summary>Time window in which a town warp's landing (1509) still moves the player.</summary>
    internal static readonly TimeSpan LandingWindow = TimeSpan.FromSeconds(5);

 /// <summary>A player's pending landing. Guarded by its own lock.</summary>
    private sealed class PendingLanding
    {
        internal readonly Lock Gate = new();
        internal uint SkillId;
        internal uint MapId;
        internal DateTime At;
    }

    private static readonly ConditionalWeakTable<PlayerState, PendingLanding> Pending = new();

 /// <summary>
 /// Records that a town warp's request was answered, so its landing may move the player. Replaces any earlier grant.
 /// </summary>
    internal static void GrantLanding(PlayerState player, uint skillId, uint mapId, DateTime nowUtc)
    {
        var p = Pending.GetValue(player, static _ => new PendingLanding());
        lock (p.Gate)
        {
            p.SkillId = skillId;
            p.MapId = mapId;
            p.At = nowUtc;
        }
    }

 /// <summary>
 /// Consumes a landing grant for <paramref name="skillId"/> on <paramref name="mapId"/> if it was given within
 /// <see cref="LandingWindow"/> before <paramref name="nowUtc"/>. Returns false otherwise, leaving the grant in place.
 /// </summary>
    internal static bool TakeLanding(PlayerState player, uint skillId, uint mapId, DateTime nowUtc)
    {
        if (!Pending.TryGetValue(player, out var p)) return false;
        lock (p.Gate)
        {
            if (p.SkillId == 0 || p.SkillId != skillId || p.MapId != mapId || nowUtc < p.At || nowUtc - p.At > LandingWindow)
                return false;
            p.SkillId = 0;
            return true;
        }
    }

    
 /// <summary>
 /// Whether a move to <paramref name="dest"/> stays on the player's current map and does not start from an instance.
 /// </summary>
    internal static bool IsInPlace(PlayerState player, Destination dest, GameDataStore data)
        => dest.MapId == player.MapId && data.GetMap(player.MapId) is not { Map_Type: MapTypeInstance };

 /// <summary>
 /// Moves <paramref name="player"/> to <paramref name="dest"/> in the form matching the source map. Returns false, with
 /// nothing sent, when the destination map is not in Map_List or the character row is missing.
 /// </summary>
    internal static async Task<bool> MoveAsync(ProudSession session, PlayerState player, Destination dest,
        IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var data = services.GetRequiredService<GameDataStore>();
        if (data.GetMap(dest.MapId) is null)
        {
            logger.LogWarning("{Name}: item warp destination map {Map} is not in Map_List; not moved", player.Name, dest.MapId);
            return false;
        }

        if (data.GetMap(player.MapId) is { Map_Type: MapTypeInstance })
        {
            
            await WarpDeparture.DropWarpEffectsAsync(player, data, services.GetRequiredService<EffectService>());
            await RidingService.DropAsync(session, player, services);
            
            var arrival = new PositionData
            {
                Area_ID = dest.MapId, Position_X = dest.X, Position_Y = dest.Y, Position_Z = dest.Z, Direction = 0,
            };
            await DungeonChannelHandOff.EnterAsync(session, player, arrival, services, logger, ct, leaveP2PGroup: false);
            return true;
        }

        var loaded = await WarpEntryBurst.PrepareAsync(player, services, logger, ct);
        if (loaded is null)
        {
            logger.LogWarning("{Name}: character {Id} not found; item warp not moved", player.Name, player.CharacterId);
            return false;
        }
        if (dest.MapId == player.MapId)
            await WarpEntryBurst.SendInPlaceAsync(session, player, loaded, dest.X, dest.Y, dest.Z, services, logger, ct);
        else
            await WarpEntryBurst.SendAsync(session, player, loaded, dest.MapId, dest.X, dest.Y, dest.Z, services, logger, ct);
        return true;
    }
}
