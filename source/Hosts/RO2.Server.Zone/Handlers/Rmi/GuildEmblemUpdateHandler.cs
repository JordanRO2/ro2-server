using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1455 ReqGuildEmblemUpdate — the guild master uploads a new guild emblem.
///
/// C2S (U32): [u32 len-4][u16][16 guild GUID @6][u16 @22]
/// [u32 imgLen @24][imgLen bytes, a DDS file @28]. The two u16s are guild-record bytes the client
/// copies in (the one @4 overlaps the GUID); nothing reads them here.
///
/// Answer: 2613 AnsUpdateGuildEmblem = u16 0 to the uploader, then
/// 2614 NfyUpdateGuildEmblem to the guild's online members, uploader included:
/// [u32 len-4][u16 0][16 GUID][u16 emblemIndex][u32 imgLen][image]. The 2614 image is the uploaded one
/// byte for byte and its index is the guild's previous index plus one, so
/// the image is stored and the index advanced (<see cref="RO2.Application.Interfaces.IGuildEmblemRepository"/>). It is a guild broadcast,
/// not a reply: every other online member of that guild receives
/// the same 2614 — same index, same image. Each 2614 is
/// followed by 2591 NfyPCViewInfoUpdate frames carrying the new index.
/// </summary>
public class GuildEmblemUpdateHandler : ZoneRmiHandlerBase<GuildEmblemUpdateHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildEmblemUpdate;
    public override string Name => "GuildEmblemUpdate";
    public GuildEmblemUpdateHandler(IServiceScopeFactory s, ILogger<GuildEmblemUpdateHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Size in bytes of a guild GUID.</summary>
    private const int GuidLength = 16;

    /// <summary>1455 body offset of the guild GUID.</summary>
    private const int GuidOff = 6;

    /// <summary>1455 body offset of the u32 image length.</summary>
    private const int ImgLenOff = 24;

    /// <summary>1455 body offset of the image bytes (a DDS file).</summary>
    private const int ImgOff = 28;

    /// <summary>Initial buffer capacity (bytes) beyond the image for the 2614 body; a sizing hint only.</summary>
    private const int NfyBodyCapacityMargin = 32;

    // Every emblem image is 640 bytes starting "DDS " (a 32x32 DXT1 DDS: 128-byte header
    // + 512 bytes of blocks). The limit keeps a modified client from broadcasting an arbitrary blob to the
    // whole guild.
    private const int MaxImageBytes = 640;
    private static ReadOnlySpan<byte> DdsMagic => "DDS "u8;

    // Refusal codes of the 2613 answer. The client shows nothing on 0 and passes any other code to the
    // guild result-message table every guild answer uses; these are the codes of that table for each condition:
    //   7  string_etc 2000000068 "Cannot Find Guild"  (GuildDelAuthority / GuildFundsInHand use it the same way)
    //   19 string_ui 8864 "Only Guild Master or the guild member have the authority to use." (the
    //      not-the-master code of the guild robe list 2929)
    //   6  string_etc 2000000067 "System Error", the table's only generic code: no entry names a bad image
    //      (GuildFundsInHand sends it for a move it could not complete)
    // The not-master refusal is reachable: the only client guard before an upload stops (string_ui 8874) when
    // the player's guild GUID is null, and nothing checks the rank. The client converts the image, writes it
    // into its own emblem cache and only then sends 1455, so a refusal shows its message but does not undo that
    // local view. No rollback is sent.
    private const ushort ResultOk = 0;
    private const ushort ResultNotYourGuild = 7;
    private const ushort ResultNotMaster = 19;
    private const ushort ResultBadImage = 6;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        var emblems = services.GetRequiredService<IGuildEmblemRepository>();
        await emblems.InitAsync();

        var span = payload.Span;
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        byte[]? image = null;
        ushort result;

        // The all-zero GUID is the guildless GUID on the wire and the emblem registry never serves an emblem
        // under it, so a guild loaded without a GUID cannot register one.
        if (guild == null || span.Length < ImgOff || !guild.Guid16.AsSpan().SequenceEqual(span.Slice(GuidOff, GuidLength))
            || guild.Guid16.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            result = ResultNotYourGuild;
        // Master only: the uploader is rank 0, and none of the four guild permissions the client names
        // (Config_Def 1000-1007: add member, withdraw, storage, notice) has an emblem bit a lower rank could hold.
        else if (guild.MasterCharId != player.CharacterId)
            result = ResultNotMaster;
        else
        {
            var imgLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(ImgLenOff, sizeof(uint)));
            if (imgLen == 0 || imgLen > MaxImageBytes || imgLen != (uint)(span.Length - ImgOff)
                || !span.Slice(ImgOff).StartsWith(DdsMagic))
                result = ResultBadImage;
            else
            {
                image = span.Slice(ImgOff, (int)imgLen).ToArray();
                result = ResultOk;
            }
        }

        Logger.LogInformation("Session {Id}: {Name} uploads guild emblem ({Size}B) -> result {Result}",
            session.SessionId, player.Name, payload.Length, result);

        if (result != ResultOk || guild == null || image == null)
        {
            await session.SendRmiAsync(ZoneS2COpcodes.AnsUpdateGuildEmblem, writer => writer.WriteUInt16(result));
            return;
        }

        var emblem = await emblems.SetAsync(guild.Guid16, image);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsUpdateGuildEmblem, writer => writer.WriteUInt16(ResultOk));
        await GuildPackets.BroadcastToGuildAsync(guild, PlayerManager, Broadcast, ZoneS2COpcodes.NfyUpdateGuildEmblem,
            BuildNfyUpdateGuildEmblem(guild.Guid16, emblem));

        // ---- 2591 NfyPCViewInfoUpdate: the crest above every online member now carries the new index ----
        // Each 2614 is followed by one 2591 per guild member in view carrying the guild
        // GUID at +110 and the new index at +126. The members already hold the image from the 2614; the 2591 is
        // what changes the crest. Sent as GuildRenameHandler does: one per online member, to that member's map
        // with the member included.
        GuildMember[] members;
        lock (guild.Gate) members = guild.Members.ToArray();
        foreach (var m in members)
        {
            var online = PlayerManager.GetPlayerByCharacterId(m.CharId);
            if (online is not { MapId: not 0 }) continue;
            var view = PcViewInfoPackets.BuildNfyPcViewInfoUpdate(online.EntityId, guild, m.CharId, emblems);
            await Broadcast.BroadcastToMapAsync(online.MapId, ZoneS2COpcodes.NfyPCViewInfoUpdate,
                writer => writer.WriteBytes(view));
        }
    }

    /// <summary>2614 (U32): VarBlob32{u16 code 0, 16 GUID, u16 emblemIndex, VarBlob32 image} — the same
    /// body as a successful 2612 AnsGuildEmblem (L = 664 for a 640-byte image).</summary>
    private static byte[] BuildNfyUpdateGuildEmblem(byte[] guid16, GuildEmblem emblem)
    {
        using var w = new PacketWriter(NfyBodyCapacityMargin + emblem.Image.Length);
        var lenPos = w.Reserve(sizeof(uint));
        var start = w.Length;
        w.WriteUInt16(ResultOk);
        w.WriteBytes(guid16);
        w.WriteUInt16(emblem.Index);
        w.WriteUInt32((uint)emblem.Image.Length);
        w.WriteBytes(emblem.Image);
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }
}
