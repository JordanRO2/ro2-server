using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// The guild vault's two histories: the item history (1491 ReqGuildStorageStatement_Item pages through it,
/// written by the 1485 put and 1486 get handlers) and the funds history (1492 ReqGuildStorageStatement_Zeny
/// pages through it, written by the 1487 deposit and 1488 withdraw handlers). Both are written only after a
/// move has really happened. A guild is identified by its 16-byte GUID, not the numeric guild id: a
/// disbanded guild's id can be reused and would inherit its history. Every method is best effort and never
/// throws: a failed write is lost, a failed read is an empty page.
/// </summary>
public interface IGuildStorageLog
{
 /// <summary>Record one item move. Best effort: the item has already moved, and a lost history line must
 /// never undo or fail it.</summary>
    Task AddAsync(byte[] guid16, string actorName, byte kind, uint itemId, int count);

 /// <summary>
 /// The item lines of the last <paramref name="days"/> days (all of them when 0), newest first, page
 /// <paramref name="page"/> of <paramref name="pageSize"/>, plus how many lines that window holds.
 /// </summary>
    Task<(long Total, IReadOnlyList<GuildStorageLogEntry> Lines)> GetPageAsync(byte[] guid16, int page, int days, int pageSize);

 /// <summary>Record one funds move (<paramref name="kind"/> 0 deposit or 1 withdraw). Best effort, as
 /// <see cref="AddAsync"/>: the zeny has already moved, and a lost history line must never undo or fail it.</summary>
    Task AddFundsAsync(byte[] guid16, string actorName, byte kind, long amount);

 /// <summary>
 /// The funds lines of the last <paramref name="days"/> days (all of them when 0), newest first, page
 /// <paramref name="page"/> of <paramref name="pageSize"/>, plus how many lines that window holds — the
 /// paging of <see cref="GetPageAsync"/> over the funds history.
 /// </summary>
    Task<(long Total, IReadOnlyList<GuildFundsLogEntry> Lines)> GetFundsPageAsync(byte[] guid16, int page, int days, int pageSize);
}
