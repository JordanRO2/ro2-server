using System.Buffers.Binary;
using RO2.Application.Interfaces;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Packets for the guild identity shown on a character's view: RMI 2591 (NfyPCViewInfoUpdate) carries the
/// guild name, GUID, emblem, robe and rank title.
/// </summary>
public static class PcViewInfoPackets
{
    internal const int BodyLength = 177;
    private const uint InnerLength = BodyLength - 4;   

    private const int OffConst1 = 4;
    private const int OffHandle = 6;
    private const int OffGuildName = 10;
    private const int OffGuid = 110;
    private const int OffEmblem = 126;
    private const int OffRobe = 128;
    private const int OffConstByte = 152;
    private const int OffRankTitle = 153;

    
    
    private const int GuildNameMaxChars = 24;

 /// <summary>
 /// Builds the 2591 body for a PC. A null <paramref name="guild"/> produces a guildless view.
 /// </summary>
 /// <param name="actorHandle">Actor handle of the PC.</param>
 /// <param name="guild">Guild the PC belongs to, or null.</param>
 /// <param name="emblems">Repository used to load the guild emblem.</param>
    public static byte[] BuildNfyPcViewInfoUpdate(uint actorHandle, Guild? guild, uint charId, IGuildEmblemRepository emblems)
        => Build(actorHandle, guild, guild == null ? string.Empty : GuildService.RankTitleOf(guild, charId), emblems);

    private static byte[] Build(uint actorHandle, Guild? guild, string rankTitle, IGuildEmblemRepository emblems)
    {
        var body = new byte[BodyLength];
        var s = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, InnerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(OffConst1, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(OffHandle, 4), actorHandle);

        if (guild != null)
        {
            
            WriteWide(s.Slice(OffGuildName, OffGuid - OffGuildName), guild.Name, GuildNameMaxChars);
            if (guild.Guid16 != null)
                guild.Guid16.AsSpan(0, Math.Min(16, guild.Guid16.Length)).CopyTo(s.Slice(OffGuid, 16));
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(OffEmblem, 2),
                emblems.IndexOf(guild.Guid16));                 
            GuildRobeRecord.WriteViewForm(s.Slice(OffRobe, OffConstByte - OffRobe)); 
        }

        s[OffConstByte] = 1;
        
        WriteWide(s.Slice(OffRankTitle), rankTitle, GuildRosterPackets.RankNameMaxChars);
        return body;
    }

 /// <summary>Builds the 2591 body for a player, using the guild it belongs to at the time of the call.</summary>
 /// <param name="player">Player to describe.</param>
 /// <param name="emblems">Repository used to load the guild emblem.</param>
    public static byte[] BuildFor(PlayerState player, IGuildEmblemRepository emblems)
        => BuildNfyPcViewInfoUpdate(player.EntityId, GuildService.Instance.GetByCharId(player.CharacterId), player.CharacterId, emblems);

 /// <summary>
 /// Sends a PC's 2591 to every client that can see that PC: the players on its map and in range.
 /// </summary>
 /// <param name="pc">PC whose view is sent.</param>
 /// <param name="guild">Guild the PC belongs to, or null for a guildless view.</param>
    public static async Task BroadcastToViewersAsync(PlayerState pc, Guild? guild, ProudSession ownSession, BroadcastService broadcast, IGuildEmblemRepository emblems)
    {
        var view = BuildNfyPcViewInfoUpdate(pc.EntityId, guild, pc.CharacterId, emblems);
        if (pc.MapId != 0)
            await broadcast.BroadcastToMapAsync(pc.MapId, ZoneS2COpcodes.NfyPCViewInfoUpdate, writer => writer.WriteBytes(view));
        else
            await ownSession.SendRmiAsync(ZoneS2COpcodes.NfyPCViewInfoUpdate, (ReadOnlyMemory<byte>)view);
    }

    private static void WriteWide(Span<byte> dst, string? s, int maxChars)
    {
        s ??= string.Empty;
        
        var n = Math.Min(Math.Min(s.Length, maxChars), dst.Length / 2 - 1);
        for (int i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(i * 2, 2), s[i]);
    }
}
