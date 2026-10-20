using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1389 ReqPartyNotice: the leader sets the party notice. Kept in PartyState.Notice
/// (<see cref="PartyService.SetNotice"/>); nothing is sent back.
/// </summary>
/// <remarks>
/// <para>
/// The layout comes from the client's builder alone: 1389 has 0 frames in the 38.8M archive, the 133.9M
/// dump and the live capture, and no client code sends it. Its builder (SendPartySearch , a
/// misnomer; proxy +728 = 1389) clamps n to 160, allocates a blob of 2n+11 bytes and writes
/// <c>[u16 2n+11][u16 58154][u32 handle][u8 n @8][n UTF-16 chars @9]</c> (ConvertBabylonStringToWide with
/// SizeInWords n), which leaves 2 unwritten bytes at the end. Its only caller is CGameMessageHandler::
/// ProcessMessage case 65694 , which passes n = wcslen + 1, so n counts the terminator. No code
/// in build 303 posts command 65694, which fits the 0 frames.
/// </para>
/// <para>
/// The body is COMPACT: <c>[u8 n][len][blob]</c>, so blob+X is not body+X. The old parse skipped 8 body
/// bytes and took the length at body+8 = blob+6, inside the handle.
/// </para>
/// </remarks>
public class PartyNoticeHandler : ZoneRmiHandlerBase<PartyNoticeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyNotice;
    public override string Name => "PartyNotice";

 /// <summary>1389's msgTag, the immediate SendPartySearch stores at blob+2 (0xE32A).</summary>
    internal const ushort MsgTagReqPartyNotice = 58154;

 /// <summary>The builder's cap on the character count at blob+8 (<c>if (a1 &gt;= 0xA0) a1 = 0xA0</c>).</summary>
    internal const int MaxChars = 0xA0;

 /// <summary>blob+8: the u8 character count n, right after the 8-byte envelope (innerSize, msgTag, handle).</summary>
    private const int CountOffset = 8;

 /// <summary>blob+9: where the UTF-16 text starts, after the 8-byte envelope and the u8 count.</summary>
    private const int TextOffset = CountOffset + 1;

    public PartyNoticeHandler(IServiceScopeFactory s, ILogger<PartyNoticeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var notice))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyNotice ({Len}B)", session.SessionId, payload.Length);
            return Task.CompletedTask;
        }

        services.GetRequiredService<PartyService>().SetNotice(session.SessionId, notice);
        return Task.CompletedTask;
    }

 /// <summary>
 /// Read a 1389 body: the compact envelope, innerSize == blob length, msgTag 58154, the u8 count n at
 /// blob+8 (at most 160) and n UTF-16 chars at blob+9, cut at the first NUL.
 /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> body, out string notice)
    {
        notice = string.Empty;
        if (!PartyAnswerPackets.TryGetCompactBlob(body, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, MsgTagReqPartyNotice, TextOffset, out _))
            return false;

        int n = blob[CountOffset];
        if (n > MaxChars || blob.Length < TextOffset + sizeof(char) * n) return false;

        var text = Encoding.Unicode.GetString(blob.Slice(TextOffset, sizeof(char) * n));
        int nul = text.IndexOf('\0');
        notice = nul >= 0 ? text[..nul] : text;
        return true;
    }
}
