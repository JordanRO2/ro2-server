using System.Runtime.CompilerServices;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat;

/// <summary>
/// The full 64-bit uid a Chat session logged in with (23001 body[4..12]), set by ChatAuthHandler.
///
/// Why it is kept apart from <c>session.CharacterId</c>: CharacterId is a u32 and holds only the low
/// dword, which is the database id every store on this server is keyed by. The client's own identity on
/// the messenger is the whole value, its character GID:
/// - retail's 2733 uid (replayed as the 23001 uid) equals that session's 2310 GID, and the C2S 23018s
/// carry that same GID as senderUid. Every 23517 lists its receiver under that receiver's own 23001 uid,
/// with every member uid above 2^32;
/// - the client writes its 2310 GID as 23018 senderUid even when it was handed something else: against our
/// own emulator, when 2733 and 23001 carried 1, the 2310 GID was still the full 64-bit value, and all C2S
/// 23018s carried that GID.
/// So anything the client matches against its own identity, or against another member's 23018 sender,
/// has to use this value: the private-room member uids of 23515/23516/23517/23504/23520/23533, and the
/// senderUid check of the 23529 relay.
///
/// A weak table rather than a field, because ProudSession (RO2.Network) has no 64-bit slot, and so the
/// entry lives exactly as long as the session object with no clean-up on disconnect.
/// </summary>
public static class MessengerUid
{
    private static readonly ConditionalWeakTable<ProudSession, StrongBox<ulong>> Uids = new();

 /// <summary>Record the uid <paramref name="session"/> logged in with (23001).</summary>
    public static void Set(ProudSession session, ulong uid) => Uids.AddOrUpdate(session, new StrongBox<ulong>(uid));

 /// <summary>The uid <paramref name="session"/> logged in with; false before its 23001.</summary>
    public static bool TryGet(ProudSession session, out ulong uid)
    {
        if (Uids.TryGetValue(session, out var box))
        {
            uid = box.Value;
            return true;
        }
        uid = 0;
        return false;
    }
}
