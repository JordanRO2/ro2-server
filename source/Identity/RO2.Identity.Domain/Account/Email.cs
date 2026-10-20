using RO2.BuildingBlocks.Domain;

namespace RO2.Identity.Domain.Account;

/// <summary>
/// Optional account email. Not used by authentication today.
/// </summary>
/// <remarks>
/// Auto-registered accounts have no email, so <see cref="None"/> is the only state produced today.
/// </remarks>
public sealed class Email : ValueObject
{
 /// <summary>The empty email value, produced by auto-registration.</summary>
    public static readonly Email None = new(null);

 /// <summary>The email address, or <see langword="null"/> when none is set.</summary>
    public string? Value { get; }

    private Email(string? value) => Value = value;

 /// <summary>True when no email is set.</summary>
    public bool IsNone => string.IsNullOrEmpty(Value);

 /// <summary>Creates an <see cref="Email"/> from a stored value.</summary>
 /// <param name="value">The stored email, or <see langword="null"/>/empty for none.</param>
 /// <returns><see cref="None"/> when blank; otherwise an <see cref="Email"/> wrapping the value.</returns>
    public static Email FromStorage(string? value) => string.IsNullOrEmpty(value) ? None : new Email(value);

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
