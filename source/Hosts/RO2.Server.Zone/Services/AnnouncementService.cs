using System.Globalization;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Sends server announcements and system messages to players.
/// </summary>
public class AnnouncementService
{
 /// <summary>The chat command that broadcasts a server notice.</summary>
    public const string NoticeCommand = "/notice";

 /// <summary>Usage text shown when <see cref="NoticeCommand"/> arguments do not parse.</summary>
    public const string NoticeUsage = "usage: /notice <RRGGBB> <text>   (e.g. /notice FFD700 Server restart in 10 minutes)";

    private readonly BroadcastService _broadcast;
    private readonly ILogger<AnnouncementService> _logger;

    public AnnouncementService(BroadcastService broadcast, ILogger<AnnouncementService> logger)
    {
        _broadcast = broadcast;
        _logger = logger;
    }

 /// <summary>
 /// Broadcasts a server notice to every online player.
 /// </summary>
 /// <remarks>
 /// Sent as 2668 OnReceiveNotice. The client shows the notice as a timed system message and as a chat line in the given colour.
 /// </remarks>
 /// <param name="message">Notice text.</param>
 /// <param name="rgb">Text colour as 0xRRGGBB.</param>
    public Task BroadcastNoticeAsync(string message, uint rgb)
    {
        _logger.LogInformation("[NOTICE] #{Rgb:X6} {Message}", rgb, message);

        var body = NoticePackets.Build2668(message, rgb, NoticePackets.PlainNotice, 0, 0);
        return _broadcast.BroadcastToAllAsync(ZoneS2COpcodes.OnReceiveNotice, writer => writer.WriteBytes(body));
    }

 /// <summary>
 /// Parses a notice chat line of the form <c>/notice &lt;RRGGBB&gt; &lt;text&gt;</c>.
 /// </summary>
 /// <remarks>
 /// The colour is required and may have a leading <c>#</c>. The text is everything after the colour, with its spacing kept.
 /// </remarks>
 /// <param name="line">Chat line to parse.</param>
 /// <param name="rgb">Parsed colour as 0xRRGGBB.</param>
 /// <param name="text">Parsed notice text.</param>
 /// <returns>True when the line is a valid notice command.</returns>
    public static bool TryParseNoticeCommand(string line, out uint rgb, out string text)
    {
        rgb = 0;
        text = string.Empty;

        var rest = line.AsSpan().TrimStart();
        int sp = rest.IndexOf(' ');
        var word = sp < 0 ? rest : rest[..sp];
        if (!word.Equals(NoticeCommand, StringComparison.OrdinalIgnoreCase) || sp < 0)
            return false;

        rest = rest[sp..].TrimStart();
        sp = rest.IndexOf(' ');
        if (sp < 0)
            return false;

        var colour = rest[..sp];
        if (colour.StartsWith("#"))
            colour = colour[1..];
        if (colour.Length != 6
            || !uint.TryParse(colour, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb))
        {
            rgb = 0;
            return false;
        }

        var body = rest[sp..].Trim();
        if (body.IsEmpty)
        {
            rgb = 0;
            return false;
        }

        text = body.ToString();
        return true;
    }

 /// <summary>Sends a system chat line (2324 OnReceiveChat) to one player.</summary>
 /// <param name="session">Target session.</param>
 /// <param name="message">Message text.</param>
    public Task SendSystemChatAsync(ProudSession session, string message)
        => session.SendInternalRmiAsync(ZoneS2COpcodes.OnReceiveChat, ChatPackets.BuildSystemLine2324(message));

 /// <summary>Sends the welcome messages to a player after they enter the world.</summary>
 /// <param name="session">Target session.</param>
 /// <param name="playerName">Name of the player.</param>
    public async Task SendWelcomeAsync(ProudSession session, string playerName)
    {
        await SendSystemChatAsync(session, $"Welcome to Ragnarok Online 2, {playerName}!");
        await SendSystemChatAsync(session, "Server is in development. Report bugs on Discord.");
    }
}
