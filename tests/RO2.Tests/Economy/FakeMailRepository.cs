using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Tests.Economy;

/// <summary>
/// In-memory <see cref="IMailRepository"/> for <see cref="EconomyFixture"/>. One id sequence for every writer, uid =
/// 0x0E4B4B8B high dword over the id (the handle shape the client expects), inbox newest first capped at 200, whole-document
/// <see cref="ReplaceAsync"/> (so a mark-read or claim that read, waited and wrote back overwrites a concurrent
/// change, as on the real store). <see cref="DeleteIfSpentAsync"/> is one atomic step. Mails are copied in and out.
/// </summary>
public sealed class FakeMailRepository(RepoScheduler scheduler) : IMailRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, MailMessage> _mails = [];
    private long _nextId;

    /// <summary>Copies of every stored mail; takes no scheduler point.</summary>
    public IReadOnlyList<MailMessage> Mails { get { lock (_gate) return [.. _mails.Values.Select(EntityCopy.Clone)]; } }

    /// <summary>Test setup: stores a copy of a ready-made mail (Id and Uid assigned when 0) without a scheduler
    /// point, and returns the stored copy.</summary>
    public MailMessage Seed(MailMessage mail)
    {
        lock (_gate)
        {
            var row = EntityCopy.Clone(mail);
            if (row.Id == 0) row.Id = (ulong)Interlocked.Increment(ref _nextId);
            else if (row.Id > (ulong)Interlocked.Read(ref _nextId)) Interlocked.Exchange(ref _nextId, (long)row.Id);
            if (row.Uid == 0) row.Uid = UidOf(row.Id);
            _mails[row.Id] = row;
            return EntityCopy.Clone(row);
        }
    }

    private static ulong UidOf(ulong id) => (0x0E4B4B8BUL << 32) | (id & 0xFFFFFFFFUL);

    public async Task<MailMessage> AddAsync(MailMessage draft, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Mail.NextId");
        var id = (ulong)Interlocked.Increment(ref _nextId);
        await scheduler.PointAsync("Mail.Add");
        lock (_gate)
        {
            draft.Id = id;
            draft.Uid = UidOf(id);
            _mails[id] = EntityCopy.Clone(draft);
            return draft;
        }
    }

    public async Task<IReadOnlyList<MailMessage>> GetInboxAsync(uint receiverCharId, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Mail.GetInbox");
        lock (_gate)
            return [.. _mails.Values.Where(m => m.ReceiverCharId == receiverCharId)
                .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).Take(200).Select(EntityCopy.Clone)];
    }

    public async Task<MailMessage?> GetByUidAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Mail.GetByUid");
        lock (_gate)
            return _mails.Values.Where(m => m.ReceiverCharId == receiverCharId && m.Uid == uid)
                .Select(EntityCopy.Clone).FirstOrDefault();
    }

    public async Task<long> CountUnreadAsync(uint receiverCharId, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Mail.CountUnread");
        lock (_gate) return _mails.Values.Count(m => m.ReceiverCharId == receiverCharId && !m.IsRead);
    }

    public async Task<long> CountLiveInboxAsync(uint receiverCharId, DateTime notBefore, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Mail.CountLiveInbox");
        lock (_gate) return _mails.Values.Count(m => m.ReceiverCharId == receiverCharId && m.SentAt >= notBefore);
    }

    public async Task ReplaceAsync(MailMessage mail, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Mail.Replace");
        lock (_gate) { if (_mails.ContainsKey(mail.Id)) _mails[mail.Id] = EntityCopy.Clone(mail); }
    }

    public async Task<bool> DeleteAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Mail.Delete");
        lock (_gate)
        {
            var m = _mails.Values.FirstOrDefault(x => x.ReceiverCharId == receiverCharId && x.Uid == uid);
            return m is not null && _mails.Remove(m.Id);
        }
    }

    /// <summary>Atomic: one flip of <c>ItemClaimed</c> false to true (an item must be attached); exactly one of any
    /// number of racing claims gets the mail, the rest get null.</summary>
    public async Task<MailMessage?> TryClaimItemAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Mail.TryClaimItem");
        lock (_gate)
        {
            var m = _mails.Values.FirstOrDefault(x => x.ReceiverCharId == receiverCharId && x.Uid == uid);
            if (m is null || m.ItemClaimed || m.AttachedItemId == 0) return null;
            m.ItemClaimed = true;
            return EntityCopy.Clone(m);
        }
    }

    /// <summary>Atomic: one flip of <c>GoldClaimed</c> false to true (<c>Gold &gt; 0</c>), exactly-one-winner like
    /// <see cref="TryClaimItemAsync"/>.</summary>
    public async Task<MailMessage?> TryClaimGoldAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Mail.TryClaimGold");
        lock (_gate)
        {
            var m = _mails.Values.FirstOrDefault(x => x.ReceiverCharId == receiverCharId && x.Uid == uid);
            if (m is null || m.GoldClaimed || m.Gold <= 0) return null;
            m.GoldClaimed = true;
            return EntityCopy.Clone(m);
        }
    }

    public async Task<bool> DeleteIfSpentAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Mail.DeleteIfSpent");
        lock (_gate)
        {
            var m = _mails.Values.FirstOrDefault(x => x.ReceiverCharId == receiverCharId && x.Uid == uid);
            return m is not null && !m.HasUnclaimedAttachment && _mails.Remove(m.Id);
        }
    }
}
