using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Per-character union memberships (the Union_Info a character joined with 1582 ReqJoinUnion and left with
/// 1583 ReqWithdrawUnion). One membership per character.
/// </summary>
public interface IUnionMembershipRepository
{
 /// <summary>The character's membership, or null when it is in no union.</summary>
    Task<UnionMembership?> GetAsync(uint characterId, CancellationToken ct = default);

 /// <summary>
 /// Records that the character joined <paramref name="unionId"/> at <paramref name="joinedAtUnix"/>, unless
 /// it already has a membership; false then (a concurrent join got there first). One membership per
 /// character holds under concurrent 1582s.
 /// </summary>
    Task<bool> JoinAsync(uint characterId, uint unionId, long joinedAtUnix, CancellationToken ct = default);

 /// <summary>Deletes the character's membership of <paramref name="unionId"/>; false when there was none
 /// to delete (another withdraw got there first, or the union differs).</summary>
    Task<bool> WithdrawAsync(uint characterId, uint unionId, CancellationToken ct = default);
}
