using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

using NetworkZoneRmiBase2 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.CancelReserveDeleteHandler>;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1578 ReqCancelReservedDeletePC, the character-select "restore the character in deletion standby"
/// confirm. Body: a bare u64 character uid (NONE family, 8 bytes; read_side U64). Answer: 2843
/// AnsCancelReservedDeletePC, the only frame sent for it.
///
/// <para>Retail answers each request with 2843 = [the same u64][u16 0]. Nothing else follows. The restore is
/// real: a restored uid enters the world again on a later connection, so retail keeps a deleted character
/// restorable. This server does the same with the soft delete ReserveDeleteHandler already makes
/// (IsDeleted + DeletedAt); see <see cref="ICharacterRepository.TryRestoreCharacterAsync"/>.</para>
///
/// <para>CLIENT BEHAVIOUR. The client sends the uid of the selected character slot when the player confirms
/// "Will you restore the character that is in deletion standby? @1 day(s) left". That dialog opens only for a
/// slot the 2842 AnsReserveDeletePC reply marked as in deletion standby, and the days left are counted from that
/// reply's timestamp against a 7-day standby period (<see cref="DeletionStandby"/>).</para>
///
/// <para>RETAIL QUIRK, deliberately NOT copied. In the same connection, retail still
/// refuses 1417 for the restored uid with 2306 code 16840, consistently. The restore only showed on the next
/// connection. That comes from retail's per-connection character snapshot, and it leaves the player
/// retrying. LoginPCHandler reads the database, so a character restored here can enter the world at
/// once.</para>
/// </summary>
public class CancelReserveDeleteHandler : NetworkZoneRmiBase2
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCancelReservedDeletePC;
    public override string Name => "CancelReserveDeletePC";

 /// <summary>2843 result when the character is restored or was never deleted. Retail sends it for a restore.</summary>
    public const ushort ResultRestored = 0;

 /// <summary>
 /// 2843 result for a refusal. The client returns without touching the slot on exactly 15314 and
 /// treats every other value as a restore. It is the same code the 2842 handler treats as its only refusal.
 /// </summary>
    public const ushort ResultNotRestored = 15314;

 /// <summary>
 /// How long a deleted character stays restorable: the client's own standby period of 7 days,
 /// counted from the 2842 timestamp.
 /// </summary>
    public static readonly TimeSpan DeletionStandby = TimeSpan.FromDays(7);

    /// <summary>Request body: a bare u64 character uid, no envelope.</summary>
    private const int RequestBodyLength = sizeof(ulong);

    /// <summary>The character id is the low dword of the character uid.</summary>
    private const ulong CharacterIdMask = 0xFFFFFFFF;

    /// <summary>2843 body length: <c>[u64 uid echo][u16 result]</c>.</summary>
    private const int AnswerBodyLength = sizeof(ulong) + sizeof(ushort);

    public CancelReserveDeleteHandler(IServiceScopeFactory s, ILogger<CancelReserveDeleteHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength || session.AccountId == null)
        {
            
            Logger.LogWarning("Session {Id}: malformed CancelReserveDeletePC ({Len} bytes, account {Account})",
                session.SessionId, payload.Length, session.AccountId);
            return;
        }
        var accountId = session.AccountId.Value;

        
        
        
        var charUid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);
        var characterId = (uint)(charUid & CharacterIdMask);

        var characters = services.GetRequiredService<ICharacterRepository>();
        var outcome = await characters.TryRestoreCharacterAsync(characterId, accountId, DateTime.UtcNow - DeletionStandby);
        var result = ResultFor(outcome);

        Logger.LogInformation("Session {Id}: CancelReserveDeletePC for UID 0x{Uid:X16}: {Outcome} -> 2843 result {Result}",
            session.SessionId, charUid, outcome, result);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsCancelReservedDeletePC,
            writer => writer.WriteBytes(BuildAnsCancelReservedDeletePC(charUid, result)));

        
        
        
        
        
        
        
    }

 /// <summary>
 /// The 2843 result for a restore outcome. 0 when the character is live afterwards, whether this
 /// request restored it or it was never deleted, since there is nothing to restore then. 15314 for
 /// any refusal: not this account's character, deleted more than <see cref="DeletionStandby"/> ago,
 /// its name taken by a live character, or the account already full.
 /// </summary>
    public static ushort ResultFor(CharacterRestoreResult outcome) => outcome switch
    {
        CharacterRestoreResult.Restored or CharacterRestoreResult.AlreadyLive => ResultRestored,
        _ => ResultNotRestored,
    };

 /// <summary>
 /// 2843 body: fixed 10 bytes, no length prefix (read_side U64,U16):
 /// <c>[u64 uid echo][u16 result]</c>. The uid is the request's own. The consumer acts
 /// only when it equals the selected slot's first 8 bytes, and reads no other field.
 /// </summary>
    public static byte[] BuildAnsCancelReservedDeletePC(ulong charUid, ushort result)
    {
        using var pw = new PacketWriter(AnswerBodyLength);
        pw.WriteUInt64(charUid);
        pw.WriteUInt16(result);
        return pw.ToArray();
    }
}
