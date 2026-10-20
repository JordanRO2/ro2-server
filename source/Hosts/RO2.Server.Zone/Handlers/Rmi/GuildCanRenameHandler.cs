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
/// RMI 1585 ReqCanRenameGuildName — the guild-rename dialog asks whether a name can be used, before the
/// real 1586. Body [u32 len][u64 item uid][u16 nChars][UTF-16 name] (GuildRenameRules.TryParse). The
/// answer 2854 AnsCanRenameGuildName is a bare u16 (2 bytes), filled
/// from a real check (GuildRenameRules.Check), plus <see cref="ResultNotMaster"/>
/// from the authority check below. The old handler answered 0 to everything.
/// </summary>
/// <remarks>
/// Not reproduced: the original server answers 44 to "Æsir", a name the format rules accept; this is unexplained.
/// A ban on Latin-1 lead letters would explain it but contradicts other guild names such as "ßlueßlooD" (U+00DF lead),
/// so GuildRenameRules.CheckFormat is left as it is.
/// </remarks>
public class GuildCanRenameHandler : ZoneRmiHandlerBase<GuildCanRenameHandler>
{
    /// <summary>
    /// 19 (0x13): the requester is not the guild master. This is the answer to a well-formed name such as "Hell's Garden"
    /// from a non-master, a name the format rules accept, so it is the requester's authority that matters. 0x13 is
    /// also the guild subsystem's not-master code: 2929 carries it to every non-master (GuildRobeChangedPackets.SelectNotMaster).
    /// The consumer hands any non-zero code to the client's guild result handler, which shows 8864 (authority) for 19
    /// instead of 7676 (prohibited word) for 44. A requester with no guild gets the same 19.
    /// </summary>
    internal const ushort ResultNotMaster = 19;

    public override ushort Opcode => ZoneC2SOpcodes.ReqCanRenameGuildName;
    public override string Name => "GuildCanRename";
    public GuildCanRenameHandler(IServiceScopeFactory s, ILogger<GuildCanRenameHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);

        ushort result;
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (!GuildRenameRules.TryParse(payload.Span, out _, out var name))
        {
            name = string.Empty;
            result = GuildRenameRules.NameNotUsable;
        }
        else if (guild == null || guild.MasterCharId != player.CharacterId)
        {
            // Renaming is the master's act (1586 renames the whole guild): answer ResultNotMaster.
            result = ResultNotMaster;
        }
        else
        {
            // The item uid is not checked here: 1586 is where it is consumed and verified.
            result = GuildRenameRules.Check(name);
        }

        Logger.LogDebug("{Name} checks guild name '{GuildName}' -> {Result}", player.Name, name, result);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsCanRenameGuildName, writer => writer.WriteUInt16(result));
    }
}
