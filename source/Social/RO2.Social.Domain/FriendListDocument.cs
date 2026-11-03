namespace RO2.Socials.Domain;

/// <summary>One owner's friend list. <c>Id</c> is the owner's CharacterId.</summary>
public sealed class FriendListDocument
{
    public uint Id { get; set; }
    public List<uint> FriendIds { get; set; } = new();
}
