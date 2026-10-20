namespace RO2.Identity.Domain.Account;

/// <summary>
/// Thrown when an aggregate invariant is violated, such as an invalid username or an empty password.
/// </summary>
public sealed class DomainException : Exception
{
 /// <summary>Initializes a new instance with the given message.</summary>
 /// <param name="message">A description of the violated invariant.</param>
    public DomainException(string message) : base(message)
    {
    }
}
