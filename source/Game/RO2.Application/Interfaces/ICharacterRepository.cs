using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

public interface ICharacterRepository
{
    Task<Character?> GetByIdAsync(uint id);
    Task<IReadOnlyList<Character>> GetCharactersByAccountAsync(uint accountId);
    Task<bool> CharacterNameExistsAsync(string name);
    Task<(Character? character, string? error)> CreateCharacterAsync(
        uint accountId, string name, byte job, byte gender, string appearanceJson,
        (int hp, int mp, int str, int agi, int intel, int wis, int vit, int luk)? stats = null);
    Task<bool> DeleteCharacterAsync(uint characterId, uint accountId);

 /// <summary>
 /// Undo <see cref="DeleteCharacterAsync"/> for a character of <paramref name="accountId"/> that was
 /// deleted at or after <paramref name="notDeletedBeforeUtc"/>. This serves RMI 1578, the
 /// character-select "restore the character in deletion standby" dialog. The soft delete is the
 /// standby state: it keeps the whole document and stamps DeletedAt, so clearing IsDeleted and
 /// DeletedAt brings the character back as it was. The restore is refused when a live character now
 /// has the name (a deleted character's name is free again for <see cref="CreateCharacterAsync"/>),
 /// or when the account already has its maximum number of live characters.
 /// </summary>
    Task<CharacterRestoreResult> TryRestoreCharacterAsync(uint characterId, uint accountId, DateTime notDeletedBeforeUtc);

    Task SaveCharacterAsync(Character character);
    Task UpdatePositionAsync(uint characterId, uint mapId, float x, float y, float z);
    Task UpdateStatsAsync(uint characterId, int hp, int mp, byte level, ulong exp);
    Task UpdateGoldAsync(uint characterId, long gold);

 /// <summary>Persist only <see cref="Character.EquipHideFlag"/> (RMI 1312). A targeted $set rather than a
 /// ReplaceOne, so it cannot overwrite fields another path is writing to the same document (the
 /// autosave bulk write, a stat allocation).</summary>
    Task UpdateEquipHideFlagAsync(uint characterId, ushort hideFlag);

 /// <summary>Persist many players' position + vitals + wallet in ONE unordered bulk write (a single
 /// DB round-trip, executed in parallel server-side). The scalable autosave path — replaces N players ×
 /// 3 sequential updates. No-op on an empty list.</summary>
    Task BulkSavePlayerStateAsync(IReadOnlyList<PlayerStateSave> saves);

 /// <summary>Persist only <see cref="Character.AllowAnyoneReview"/> (RMI 1697): a targeted $set, so it cannot
 /// overwrite what the autosave bulk write is writing to the same document.</summary>
    Task UpdateAllowAnyoneReviewAsync(uint characterId, bool allow, CancellationToken ct = default);

 /// <summary>Persist the appearance JSON, gender and race of a character that is not deleted (RMI 1705), as
 /// one targeted $set. True when a non-deleted row matched (matched, not modified: an unchanged value still
 /// counts).</summary>
    Task<bool> UpdateAppearanceAsync(uint characterId, string appearanceJson, byte gender, byte race, CancellationToken ct = default);

 /// <summary>Persist the appearance JSON and, when <paramref name="job"/> has a value, the job byte, as one
 /// targeted $set (profession change). Matches the id only, deleted or not.</summary>
    Task UpdateAppearanceAndJobAsync(uint characterId, string? appearanceJson, byte? job, CancellationToken ct = default);

 /// <summary>The character that is not deleted whose name equals <paramref name="name"/> ignoring case
 /// (anchored, escaped, case-insensitive regex), or null. The stored spelling is returned.</summary>
    Task<Character?> FindByNameIgnoreCaseAsync(string name, CancellationToken ct = default);

 /// <summary>The character that is not deleted whose name is exactly <paramref name="name"/> (case-sensitive),
 /// or null. The exact first step of the mail recipient lookup, before <see cref="FindByNameIgnoreCaseAsync"/>.</summary>
    Task<Character?> FindByExactNameAsync(string name, CancellationToken ct = default);

 /// <summary>Whether the account has at least one character that is not deleted.</summary>
    Task<bool> AccountHasLiveCharacterAsync(uint accountId, CancellationToken ct = default);

 /// <summary>Whether a character that is not deleted carries <paramref name="name"/>, ignoring case. False for
 /// a blank name. Exact match first, then a case-insensitive regex (unlike <see cref="CharacterNameExistsAsync"/>,
 /// which is exact only).</summary>
    Task<bool> NameExistsIgnoreCaseAsync(string name, CancellationToken ct = default);

 /// <summary>Rename a character with a targeted $set guarded on <paramref name="oldName"/> and not-deleted.
 /// True when a row was modified.</summary>
    Task<bool> RenameAsync(uint characterId, string oldName, string newName, CancellationToken ct = default);

 /// <summary>Persist only <see cref="Character.BankSlots"/>: a whole-row replace from a login snapshot would
 /// put its stale live fields (wallet, position, HP) back.</summary>
    Task SetBankSlotsAsync(uint characterId, byte slots, CancellationToken ct = default);

 /// <summary>The characters with these ids, deleted ones included (the caller filters).</summary>
    Task<IReadOnlyList<Character>> GetByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default);

 /// <summary>How many of these ids are a character that is not deleted.</summary>
    Task<int> CountLiveByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default);

 /// <summary>The characters with these ids that are not deleted.</summary>
    Task<IReadOnlyList<Character>> GetLiveByIdsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default);

 /// <summary>The characters that are not deleted whose name equals one of <paramref name="names"/> under the
 /// case-insensitive collation ("en", secondary strength) the Chat block list resolves names with.</summary>
    Task<IReadOnlyList<Character>> FindByNamesCollatedAsync(IReadOnlyCollection<string> names, CancellationToken ct = default);
}

/// <summary>Outcome of <see cref="ICharacterRepository.TryRestoreCharacterAsync"/>.</summary>
public enum CharacterRestoreResult
{
 /// <summary>This call cleared IsDeleted; the character is live again.</summary>
    Restored,
 /// <summary>The account's character is not deleted, so there was nothing to restore.</summary>
    AlreadyLive,
 /// <summary>No character with this id belongs to the account.</summary>
    NotFound,
 /// <summary>Deleted before the cutoff, or deleted without a DeletedAt stamp.</summary>
    Expired,
 /// <summary>A live character now has the name.</summary>
    NameTaken,
 /// <summary>The account already has its maximum number of live characters.</summary>
    AccountFull,
}

/// <summary>One player's frequently-changing persistent state, for the bulk autosave.</summary>
public readonly record struct PlayerStateSave(
    uint CharacterId, uint MapId, float X, float Y, float Z,
    int Hp, int MaxHp, int Mp, int MaxMp, byte Level, ulong Exp, long Gold, long BankGold, byte Job, ushort BindIndex,
    float FacingYaw);
