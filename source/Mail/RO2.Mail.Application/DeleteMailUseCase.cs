using RO2.Mails.Domain;

namespace RO2.Mails.Application;

/// <summary>
/// The store side of a 1398 delete: one repository call. Unclaimed attachments do not stop it, and false (nothing there)
/// is not a failure: the host sends 2448 either way.
/// </summary>
public sealed class DeleteMailUseCase(IMailRepository mails)
{
    /// <summary>Deletes the reader's mail; false when there was none.</summary>
    public Task<bool> DeleteAsync(uint charId, ulong uid) => mails.DeleteAsync(charId, uid);
}
