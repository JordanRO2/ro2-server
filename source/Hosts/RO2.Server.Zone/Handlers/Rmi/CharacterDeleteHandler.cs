using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using NetworkZoneRmiBase = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.CharacterDeleteHandler>;
using RO2.Characters.Domain;
using RO2.Characters.Application;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1416: immediate (non-reserved) character delete. Answered with S2C RMI 2308 OnDeleteCharacterACK.
/// </summary>
/// <remarks>
/// The live client uses <see cref="ReserveDeleteHandler"/> (RMI 1577) for its delete button. This handler
/// implements the request as the client's builder defines it. The request body holds the character slot uid
/// at body offset 10.
/// <para>
/// The answer is a compact blob <c>[u16 innerSize 10][u16 msgTag][u32 accountId][u16 result]</c>
/// (<see cref="PacketWriter.WriteCompactBABody"/>). The client refreshes the character list after every answer,
/// so this handler does not push a list itself.
/// </para>
/// </remarks>
public class CharacterDeleteHandler : NetworkZoneRmiBase
{
    public override ushort Opcode => ZoneC2SOpcodes.Packet_CLMSR_SLOT_DELETEPC;
    public override string Name => "DeleteCharacter";

 /// <summary>Message tag of the 2308 blob.</summary>
    internal const ushort OnDeleteCharacterAckMsgTag = 56007;

 /// <summary>Result code for a successful delete.</summary>
    internal const ushort ResultDeleted = 0;

 /// <summary>Result code for a refused delete ("Cannot delete character.").</summary>
    internal const ushort ResultCannotDelete = 15304;

 /// <summary>
 /// Result code for the character of a guild master, which must disband the guild first. The same value is
 /// used by <see cref="ReserveDeleteHandler.ResultGuildMaster"/>.
 /// </summary>
    internal const ushort ResultGuildMaster = 15314;

 /// <summary>Request offset of the u64 character slot uid: the 2-byte compact envelope plus the 8-byte inner header.</summary>
    private const int CharacterUidOffset = 10;

 /// <summary>Smallest request that holds the character slot uid.</summary>
    private const int MinRequestLength = CharacterUidOffset + sizeof(ulong);

 /// <summary>The character id is the low 32 bits of the slot uid.</summary>
    private const ulong CharacterIdMask = 0xFFFFFFFF;

    public CharacterDeleteHandler(IServiceScopeFactory scopeFactory, ILogger<CharacterDeleteHandler> logger)
        : base(scopeFactory, logger)
    {
    }

 /// <summary>The 2308 body: a compact envelope around a 10-byte blob.</summary>
    internal static byte[] BuildOnDeleteCharacterAck(uint accountId, ushort result)
    {
        using var w = new PacketWriter(16);
        w.WriteCompactBABody(p =>
        {
            p.WriteUInt16(OnDeleteCharacterAckMsgTag); 
            p.WriteUInt32(accountId);                  
            p.WriteUInt16(result);                     
        });
        return w.ToArray();
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogInformation("Session {Id}: DeleteCharacter (RMI 1416) received ({Size} bytes)",
            session.SessionId, payload.Length);

        if (payload.Length < MinRequestLength || session.AccountId == null)
            return;
        uint accountId = session.AccountId.Value;

        var reader = new PacketReader(payload.Span);
        reader.Skip(CharacterUidOffset);
        var charUid = reader.ReadUInt64();
        var characterId = (uint)(charUid & CharacterIdMask);

        var repo = services.GetRequiredService<ICharacterRepository>();
        var guilds = services.GetRequiredService<IGuildMembershipView>();
        var result = await DeleteCharacterFlow.DeleteAsync(accountId, characterId, repo, guilds);

        switch (result.Outcome)
        {
            case DeleteCharacterFlow.DeleteOutcome.NotLive:
                Logger.LogWarning("Session {Id}: DeleteCharacter for UID 0x{Uid:X16}, not a live character of account {Account}",
                    session.SessionId, charUid, accountId);
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnDeleteCharacterACK,
                    BuildOnDeleteCharacterAck(accountId, ResultCannotDelete), ct);
                return;

            case DeleteCharacterFlow.DeleteOutcome.GuildMaster:
                Logger.LogInformation("Session {Id}: DeleteCharacter refused, '{Name}' is a guild master",
                    session.SessionId, result.Name);
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnDeleteCharacterACK,
                    BuildOnDeleteCharacterAck(accountId, ResultGuildMaster), ct);
                return;

            case DeleteCharacterFlow.DeleteOutcome.RaceLost:
                // The delete lost a race with another; on purpose, nothing is answered.
                Logger.LogWarning("Session {Id}: DeleteCharacter for '{Name}' lost a race with another delete",
                    session.SessionId, result.Name);
                return;

            default:
                Logger.LogInformation("Session {Id}: Deleted character '{Name}' (ID: {CharId})",
                    session.SessionId, result.Name, characterId);
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnDeleteCharacterACK,
                    BuildOnDeleteCharacterAck(accountId, ResultDeleted), ct);
                return;
        }
    }
}
