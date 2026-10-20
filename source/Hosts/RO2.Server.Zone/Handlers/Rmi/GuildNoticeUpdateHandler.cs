using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1428 ReqGuildNoticeUpdate: sets the guild notice.</summary>
/// <remarks>
/// <para>
/// The request is [u32 length - 4][16-byte guild GUID][u16 character count][count UTF-16 characters]. The count includes an optional trailing colour code (U+F800 to U+FFFF). The sender receives a 2563 acknowledgement with the result and the guild GUID. When the notice is accepted, every online member receives a 2564 NfyGuildNoticeUpdated.
/// </para>
/// <para>
/// A notice longer than the limit is refused with code 15. A sender without a guild receives code 7.
/// </para>
/// </remarks>
public class GuildNoticeUpdateHandler : ZoneRmiHandlerBase<GuildNoticeUpdateHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildNoticeUpdate;
    public override string Name => "GuildNoticeUpdate";
    public GuildNoticeUpdateHandler(IServiceScopeFactory s, ILogger<GuildNoticeUpdateHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Result 0: the notice was set.</summary>
    internal const ushort NoticeSet = 0;

 /// <summary>Result 7: the sender is not in a guild.</summary>
    internal const ushort NoGuild = 7;

 /// <summary>Result 15: the notice is too long.</summary>
    internal const ushort NoticeTooLong = 15;

 /// <summary>Maximum notice length in UTF-16 characters, colour code included.</summary>
    internal const int MaxNoticeCount = 100;

 /// <summary>Size in bytes of the request's leading length field, [u32 length - 4].</summary>
    private const int LengthFieldSize = 4;

 /// <summary>Size in bytes of one UTF-16 notice character.</summary>
    private const int CharSize = 2;

 /// <summary>Size in bytes of the 2563 result code field (u16).</summary>
    private const int ResultFieldSize = 2;

 /// <summary>Size in bytes of the notice character count field (u16).</summary>
    private const int CountFieldSize = 2;

 /// <summary>Offset of the notice character count in the request body: after the length field and the guild GUID.</summary>
    private const int NoticeCountOffset = LengthFieldSize + Guild.GuidLength;

 /// <summary>
 /// Mask of the colour-code character range, U+F800 to U+FFFF. The client appends one such character as the last
 /// character of the notice (U+F801 is the default colour).
 /// </summary>
    private const int ColourCodeMask = 0xF800;

 /// <summary>Size in bytes of the 2563 answer body: [u16 result][16-byte guild GUID].</summary>
    private const int AnsBodySize = ResultFieldSize + Guild.GuidLength;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);

        var (count, notice) = ReadNotice(payload.Span);
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        ushort result = Validate(guild != null, count);
        if (result == NoticeSet) await GuildService.Instance.SetNoticeAsync(guild!, notice);

        Logger.LogInformation("Session {Id}: {Name} sets guild notice ({Count} chars with its colour code) -> result {Result}",
            session.SessionId, player.Name, count, result);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildUpdateNotice, (ReadOnlyMemory<byte>)BuildAnsGuildUpdateNotice(result, guild?.Guid16));

        if (result == NoticeSet)
        {
            var body = GuildPackets.BuildNfyGuildNoticeUpdated(guild!);
            await GuildPackets.BroadcastToGuildAsync(guild!, PlayerManager, Broadcast, ZoneS2COpcodes.NfyGuildNoticeUpdated, body);
        }
    }

 /// <summary>Returns the 2563 result code for a notice of <paramref name="count"/> characters (colour code included), given whether the sender is in a guild.</summary>
    internal static ushort Validate(bool inGuild, int count)
        => !inGuild ? NoGuild : count > MaxNoticeCount ? NoticeTooLong : NoticeSet;

 /// <summary>
 /// Reads the notice from the request body. Returns the declared character count, used for the length check, and the text without the trailing colour code. The colour code is appended again when the notice is sent.
 /// </summary>
    internal static (int Count, string Text) ReadNotice(ReadOnlySpan<byte> body)
    {
        if (body.Length < NoticeCountOffset + CountFieldSize) return (0, string.Empty);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(NoticeCountOffset));
        int available = Math.Min(n, (body.Length - NoticeCountOffset - CountFieldSize) / CharSize);
        var chars = new System.Text.StringBuilder(available);
        for (int i = 0; i < available; i++)
        {
            char c = (char)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(NoticeCountOffset + CountFieldSize + i * CharSize));
            if (c == '\0') break;
            chars.Append(c);
        }
        if (chars.Length > 0 && (chars[^1] & ColourCodeMask) == ColourCodeMask) chars.Length--;   // drop the trailing colour code
        return (n, chars.ToString());
    }

 /// <summary>
 /// Builds the 2563 AnsGuildUpdateNotice body: [u16 result][16-byte guild GUID]. The body is a fixed 18 bytes. A sender without a guild gets the zero GUID.
 /// </summary>
    internal static byte[] BuildAnsGuildUpdateNotice(ushort result, byte[]? guildGuid16)
    {
        var body = new byte[AnsBodySize];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        if (guildGuid16 != null) guildGuid16.AsSpan(0, Math.Min(Guild.GuidLength, guildGuid16.Length)).CopyTo(body.AsSpan(ResultFieldSize));
        return body;
    }
}
