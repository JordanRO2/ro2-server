using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1600 ReqSetPresentPassword, which sets or changes the account's gift password.
/// The request body is [WStr oldPassword][WStr newPassword]. The reply is AnsSetPresentPassword (2872),
/// a fixed [u16 result].
/// </summary>
/// <remarks>
/// An account with no gift password may set one with an empty old password. The status reply (2871) reports
/// whether a password is stored (01 means none, 00 means one), which tells the client which flow to offer.
/// When no password is stored, the old password is not checked.
/// </remarks>
public class SetPresentPasswordHandler : ZoneRmiHandlerBase<SetPresentPasswordHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSetPresentPassword;
    public override string Name => "SetPresentPassword";
    public SetPresentPasswordHandler(IServiceScopeFactory s, ILogger<SetPresentPasswordHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Generic refusal (result 1) for requests a real client cannot send, such as a malformed body,
 /// or for a store that cannot be written.
 /// </summary>
    internal const ushort ResultRefused = 1;

 /// <summary>
 /// Result 24206 (incorrect password): a change whose old password does not match the stored one.
 /// </summary>
    internal const ushort ResultIncorrectPassword = 24206;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        int pos = 0;
        var span = payload.Span;
        if (!ProudWStr.TryRead(span, ref pos, out var oldPassword, 16) || !ProudWStr.TryRead(span, ref pos, out var newPassword, 16))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqSetPresentPassword ({Len} bytes)", player.Name, payload.Length);
            await SendResultAsync(session, ResultRefused, ct);
            return;
        }

        
        var store = services.GetRequiredService<IPresentPasswordRepository>();
        ushort result;
        try
        {
            bool hasPassword = await store.HasPasswordAsync(player.AccountId, ct);
            bool oldMatches = hasPassword && await store.VerifyAsync(player.AccountId, oldPassword, ct);
            
            result = Refusal(hasPassword, oldMatches, newPassword)
                ?? (await store.SetAsync(player.AccountId, oldPassword, newPassword, ct) ? (ushort)0 : ResultIncorrectPassword);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Present password store failed for account {Account}", player.AccountId);
            result = ResultRefused;
        }
        
        Logger.LogInformation("{Name} (account {Account}) sets present password: {Result}", player.Name, player.AccountId,
            result == 0 ? "stored"
            : result == ResultIncorrectPassword ? "refused (old password does not match the stored one)"
            : PresentPasswordRepository.IsValidPassword(newPassword) ? "refused (the store failed)"
            : "refused (new password is not 4 digits)");

        
        await SendResultAsync(session, result, ct);
    }

 /// <summary>
 /// Returns the refusal result for a set or change request, or null when the new password may be stored.
 /// </summary>
 /// <remarks>
 /// A stored password is checked against the request's old password first. When no password is stored,
 /// the old password is not checked. A new password that is not four digits is refused.
 /// </remarks>
    internal static ushort? Refusal(bool hasPassword, bool oldMatches, string newPassword) =>
        hasPassword && !oldMatches ? ResultIncorrectPassword
        : !PresentPasswordRepository.IsValidPassword(newPassword) ? ResultRefused
        : null;

    private static Task SendResultAsync(ProudSession session, ushort result, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsSetPresentPassword, w => w.WriteUInt16(result), ct);
}
