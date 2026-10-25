namespace RO2.Characters.Domain;

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
