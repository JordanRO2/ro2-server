namespace RO2.Domain.Entities;

/// <summary>
/// User account domain entity
/// </summary>
public class Account
{
    public uint Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? SecondPasswordHash { get; set; }
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLogin { get; set; }
    public bool IsBanned { get; set; }
    public DateTime? BanExpires { get; set; }
    public string? BanReason { get; set; }
    public byte AccessLevel { get; set; }
}
