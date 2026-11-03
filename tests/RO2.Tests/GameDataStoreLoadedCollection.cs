using Xunit;

namespace RO2.Tests;

/// <summary>Every test class that calls <c>GameDataStore.LoadAsync</c> belongs here: LoadAsync swaps the process-wide <c>GameDataStore.Loaded</c> that wire builders read as a fallback, so those classes must not overlap each other or any parallel test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameDataStoreLoadedCollection
{
    public const string Name = "GameDataStoreLoaded";
}
