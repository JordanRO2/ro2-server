using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Collections.Application;
using RO2.Collections.Domain;
using RO2.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builds the <see cref="PetCollectionUseCase"/> for a handler's scope and logger. Not registered in DI (like <see cref="MonsterCollectionUseCaseFactory"/>):
/// the logger is the handler's. The host side owns the ambient pet table (passed in as rows) and the storage-error log lines.
/// </summary>
public static class PetCollectionUseCaseFactory
{
    /// <summary>Creates the use case over the scoped book repository and the loaded PetInfo rows.</summary>
    public static PetCollectionUseCase From(IServiceProvider services, ILogger log) =>
        new(services.GetRequiredService<ICollectionRepository>(),
            services.GetRequiredService<GameDataStore>().PetInfo,
            (op, characterId, ex) =>
            {
                switch (op)
                {
                    case PetCollectionStorageOp.BookLoad:
                        log.LogWarning(ex, "Collection: load failed for char {Char}; using empty state", characterId); break;
                    case PetCollectionStorageOp.BookSave:
                        log.LogWarning(ex, "Collection: save failed for char {Char}", characterId); break;
                }
            });
}
