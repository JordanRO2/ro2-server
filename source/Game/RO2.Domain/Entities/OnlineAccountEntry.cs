namespace RO2.Domain.Entities;

/// <summary>One online account: which Zone connection holds it and when that connection last proved it was alive.</summary>
public sealed class OnlineAccountEntry
{
 /// <summary>The account id.</summary>
    public uint AccountId { get; set; }

 /// <summary><c>{process instance}:{zone session id}</c> of the connection that holds the account.</summary>
    public string Owner { get; set; } = string.Empty;

 /// <summary>Last heartbeat of that connection (UTC).</summary>
    public DateTime SeenAt { get; set; }

 /// <summary>Set by Login when another login of the account asked for this connection to be dropped.</summary>
    public DateTime? KickRequestedAt { get; set; }
}
