using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Collections.Application;
using RO2.Collections.Domain;
using RO2.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builds the <see cref="MonsterCollectionUseCase"/> for a handler's scope and logger. Not registered in DI (like <see cref="CollectionService.From"/>):
/// the logger is the handler's. The host side owns the ambient pet table (the active-pet flag derivation) and the storage-error log lines.
/// </summary>
public static class MonsterCollectionUseCaseFactory
{
    /// <summary>Creates the use case over the scoped book and progress repositories.</summary>
    public static MonsterCollectionUseCase From(IServiceProvider services, ILogger log)
    {
        var data = services.GetRequiredService<GameDataStore>();
        return new MonsterCollectionUseCase(
            services.GetRequiredService<ICollectionRepository>(),
            services.GetRequiredService<IMonsterCollectionProgressRepository>(),
            doc => PetCollectionBook.ActiveFlag(data, doc.ActivePetId, doc.LoyaltyOf(doc.ActivePetId)),
            (op, characterId, ex) =>
            {
                switch (op)
                {
                    case MonsterCollectionStorageOp.BookLoad:
                        log.LogWarning(ex, "Collection: load failed for char {Char}; using empty state", characterId); break;
                    case MonsterCollectionStorageOp.BookSave:
                        log.LogWarning(ex, "Collection: save failed for char {Char}", characterId); break;
                    case MonsterCollectionStorageOp.ProgressLoad:
                        log.LogWarning(ex, "MonsterCollection: progress load failed for char {Char}; using level 1 / exp 0", characterId); break;
                    case MonsterCollectionStorageOp.ProgressSave:
                        log.LogWarning(ex, "MonsterCollection: progress save failed for char {Char}", characterId); break;
                }
            });
    }
}
