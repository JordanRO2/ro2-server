namespace RO2.Identity.Domain.Account;

/// <summary>
/// Abstraction over the current time, so the domain does not read the system clock directly.
/// </summary>
public interface IClock
{
 /// <summary>The current UTC instant.</summary>
    DateTime UtcNow { get; }
}
