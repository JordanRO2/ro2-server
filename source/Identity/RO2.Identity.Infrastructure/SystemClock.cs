using RO2.Identity.Domain.Account;

namespace RO2.Identity.Infrastructure;

/// <summary>
/// System clock implementation of <see cref="IClock"/> that returns the current UTC time.
/// </summary>
/// <remarks>
/// This is the only place that reads <c>DateTime.UtcNow</c>. The domain obtains the current time through
/// <see cref="IClock"/>.
/// </remarks>
public sealed class SystemClock : IClock
{
 /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}
