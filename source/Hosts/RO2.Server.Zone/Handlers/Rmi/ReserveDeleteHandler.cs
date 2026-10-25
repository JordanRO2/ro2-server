using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using NetworkZoneRmiBase = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReserveDeleteHandler>;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1577 (ReqReserveDeletePC), the character-select delete. The request body is the character
/// uid as a u64. The answer is 2842 AnsReserveDeletePC.
/// </summary>
/// <remarks>
/// A deleted character is marked deleted and can be restored during the client's standby period (see
/// <c>CancelReserveDeleteHandler</c>). Guild masters cannot delete their character and receive a refusal
/// result instead.
/// </remarks>
public class ReserveDeleteHandler : NetworkZoneRmiBase
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqReserveDeletePC;
    public override string Name => "ReserveDeletePC";

    /// <summary>2842 result for a successful delete.</summary>
    public const ushort ResultDeleted = 0;

    /// <summary>
    /// 2842 result for a guild master, who cannot delete the character. The client answers it with the
    /// "Unable to delete the Guild Master" message and keeps the character.
    /// </summary>
    public const ushort ResultGuildMaster = 15314;

    /// <summary>Request length: the character uid (u64).</summary>
    private const int RequestLength = 8;

    /// <summary>Mask for the low dword of the character uid, which is the character id (the high dword is a uid tag).</summary>
    private const ulong CharacterIdMask = 0xFFFFFFFF;

    /// <summary>Length of the 2842 body: <c>[u64 uid][u16 result][u64 unix seconds]</c>.</summary>
    private const int AnswerLength = 18;

    public ReserveDeleteHandler(IServiceScopeFactory s, ILogger<ReserveDeleteHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength || session.AccountId == null) return;
        var accountId = session.AccountId.Value;

        // Payload: [u64 charUid], no header. The low dword is the character id.
        var charUid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);
        var characterId = (uint)(charUid & CharacterIdMask);

        var characters = services.GetRequiredService<ICharacterRepository>();
        var character = await characters.GetByIdAsync(characterId);
        if (character == null || character.AccountId != accountId)
        {
            // Not a live character of this account: already deleted, or a uid the account never had. Nothing was
            // deleted, so no success is claimed.
            Logger.LogWarning("Session {Id}: ReserveDeletePC for UID 0x{Uid:X16}, not a live character of account {Account}",
                session.SessionId, charUid, accountId);
            return;
        }

        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        if (IsGuildMaster(GuildService.Instance.GetByCharId(characterId), characterId))
        {
            // The character is kept; the timestamp is still the server's clock.
            Logger.LogInformation("Session {Id}: ReserveDeletePC refused, '{Name}' is a guild master",
                session.SessionId, character.Name);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsReserveDeletePC,
                writer => writer.WriteBytes(BuildAnsReserveDeletePC(charUid, ResultGuildMaster, DateTimeOffset.UtcNow)));
            return;
        }

        var deleted = await characters.DeleteCharacterAsync(characterId, accountId);
        if (!deleted)
        {
            // The document stopped matching between the read above and this write: another delete took it.
            Logger.LogWarning("Session {Id}: ReserveDeletePC for '{Name}' lost a race with another delete",
                session.SessionId, character.Name);
            return;
        }

        Logger.LogInformation("Session {Id}: Deleted character '{Name}' (UID 0x{Uid:X16})",
            session.SessionId, character.Name, charUid);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsReserveDeletePC,
            writer => writer.WriteBytes(BuildAnsReserveDeletePC(charUid, ResultDeleted, DateTimeOffset.UtcNow)));

        // No character-list push: the client refreshes the list itself after a result other than 15314, and the
        // zone client-init handler answers that request.
    }

    /// <summary>
    /// Whether <paramref name="characterId"/> is the master of <paramref name="guild"/>.
    /// </summary>
    public static bool IsGuildMaster(Guild? guild, uint characterId)
    {
        if (guild == null) return false;
        lock (guild.Gate) return guild.MasterCharId == characterId;
    }

    /// <summary>
    /// Builds the 2842 body: an 18-byte record <c>[u64 uid][u16 result][u64 unix seconds]</c>. The uid is echoed
    /// from the request, and the timestamp is the server's UTC time at the answer.
    /// </summary>
    public static byte[] BuildAnsReserveDeletePC(ulong charUid, ushort result, DateTimeOffset now)
    {
        using var pw = new PacketWriter(AnswerLength);
        pw.WriteUInt64(charUid);
        pw.WriteUInt16(result);
        pw.WriteUInt64((ulong)now.ToUnixTimeSeconds());
        return pw.ToArray();
    }
}
