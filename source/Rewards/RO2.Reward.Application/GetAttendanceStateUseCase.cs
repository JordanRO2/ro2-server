using RO2.Rewards.Domain;

namespace RO2.Rewards.Application;

/// <summary>Reads an account's attendance state, which answers the show-attendance-state request (1692).</summary>
public sealed class GetAttendanceStateUseCase
{
    private readonly IAccountGiftRepository _gifts;

    /// <summary>Creates the use case over the gift repository.</summary>
    public GetAttendanceStateUseCase(IAccountGiftRepository gifts) => _gifts = gifts;

 /// <summary>The account's attendance; all zero before the first attended day. A storage error propagates.</summary>
    public Task<AccountAttendance> ExecuteAsync(uint accountId, CancellationToken ct = default)
        => _gifts.GetAttendanceAsync(accountId, ct);
}
