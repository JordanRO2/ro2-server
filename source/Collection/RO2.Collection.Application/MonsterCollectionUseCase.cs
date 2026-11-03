using RO2.Collections.Domain;
using RO2.GameData.Tables;

namespace RO2.Collections.Application;

/// <summary>Which storage call of <see cref="MonsterCollectionUseCase"/> failed; the host logs it and carries on.</summary>
public enum MonsterCollectionStorageOp
{
    /// <summary>The collection book load threw; the flow went on with an empty book.</summary>
    BookLoad,
    /// <summary>The collection book save threw; the change is lost.</summary>
    BookSave,
    /// <summary>The progress load threw; the flow went on with an empty document (level 1 / exp 0).</summary>
    ProgressLoad,
    /// <summary>The progress save threw; the change is lost.</summary>
    ProgressSave,
}

/// <summary>The outcome of one DNA feed: the decision (<see cref="MonsterFeedKind"/>, level, exp, 2923 result) and the registered-collection count for the 2913.</summary>
public readonly record struct MonsterFeedResult(MonsterFeedKind Kind, byte Level, uint Exp, ushort Result, int Registered);

/// <summary>The outcome of a 1621 selection: whether the collection is registered, and the wire state (level 0 when it is not).</summary>
public readonly record struct MonsterSelection(bool Registered, MonsterCollectionState State);

/// <summary>
/// The monster-collection flows over the book and the progress document: the 1623 feed (decide with <see cref="MonsterFeedDecision"/>,
/// register on the first DNA, persist level and exp), the 1621 select and the 1620 book read. Storage errors are reported through
/// <c>onStorageError</c> and swallowed (a load error yields an empty document, a save error drops the change), so the client is still answered.
/// The CollectMonster rows come in as arguments and the active-pet flag derivation as a delegate. The bag check, the DNA consume, the wire sends
/// and their order stay in the Zone host.
/// </summary>
/// <remarks>
/// Every update is a whole-document read-modify-write with no compare-and-set: two concurrent feeds of one character can lose an increment (known, preserved).
/// </remarks>
public sealed class MonsterCollectionUseCase(
    ICollectionRepository book,
    IMonsterCollectionProgressRepository progress,
    Func<CollectionDocument, byte> activePetFlag,
    Action<MonsterCollectionStorageOp, uint, Exception>? onStorageError = null)
{
    /// <summary>Feeds one DNA to <paramref name="npcId"/>: decides, registers on the first DNA, and saves the new level and exp. A refused feed (<see cref="MonsterFeedKind.MaxLevel"/>) writes nothing.</summary>
    public async Task<MonsterFeedResult> FeedAsync(uint characterId, uint npcId, IEnumerable<CollectMonster> rows, CancellationToken ct = default)
    {
        var doc = await LoadBookAsync(characterId, ct);
        var prog = await LoadProgressAsync(characterId, ct);
        var entry = prog.Find(npcId);

        var decision = MonsterFeedDecision.Decide(rows, npcId, doc.RegisteredMonsterIds.Contains(npcId), entry?.Level ?? 1, entry?.Exp ?? 0);
        if (decision.Kind == MonsterFeedKind.MaxLevel)
            return new(decision.Kind, decision.Level, decision.Exp, decision.Result, doc.RegisteredMonsterIds.Count);
        int registered = decision.Kind == MonsterFeedKind.Registered
            ? await RegisterAsync(characterId, npcId, ct)
            : doc.RegisteredMonsterIds.Count;

        if (entry is null)
        {
            entry = new MonsterCollectionEntry { NpcId = npcId };
            prog.Entries.Add(entry);
        }
        entry.Level = decision.Level;
        entry.Exp = decision.Exp;
        await SaveProgressAsync(prog, ct);
        return new(decision.Kind, decision.Level, decision.Exp, decision.Result, registered);
    }

    /// <summary>Registers a monster collection (idempotent; id 0 is never registered) and returns the registered count. Reloads the book.</summary>
    public async Task<int> RegisterAsync(uint characterId, uint collectionId, CancellationToken ct = default)
    {
        var doc = await LoadBookAsync(characterId, ct);
        if (collectionId != 0 && !doc.RegisteredMonsterIds.Contains(collectionId))
        {
            doc.RegisteredMonsterIds.Add(collectionId);
            await SaveBookAsync(doc, ct);
        }
        return doc.RegisteredMonsterIds.Count;
    }

    /// <summary>Records <paramref name="collectionId"/> as selected (not registering it) and returns its wire state at the stored progress.</summary>
    public async Task<MonsterSelection> SelectAsync(uint characterId, uint collectionId, IEnumerable<CollectMonster> rows, CancellationToken ct = default)
    {
        var doc = await LoadBookAsync(characterId, ct);
        if (collectionId != 0 && !doc.SelectedMonsterSkills.Contains(collectionId))
        {
            doc.SelectedMonsterSkills.Add(collectionId);
            await SaveBookAsync(doc, ct);
        }
        bool registered = doc.RegisteredMonsterIds.Contains(collectionId);
        var prog = await LoadProgressAsync(characterId, ct);
        return new(registered, CollectionRules.State(rows, prog, collectionId, registered));
    }

    /// <summary>The monster book records (1620): one per registered collection, ordered by id, at the stored level and exp.</summary>
    public async Task<List<MonsterCollectionState>> OpenBookAsync(uint characterId, IEnumerable<CollectMonster> rows, CancellationToken ct = default)
    {
        var doc = await LoadBookAsync(characterId, ct);
        var prog = await LoadProgressAsync(characterId, ct);
        return CollectionRules.BookStates(rows, doc.RegisteredMonsterIds, prog);
    }

    private async Task<CollectionDocument> LoadBookAsync(uint characterId, CancellationToken ct)
    {
        CollectionDocument doc;
        try
        {
            doc = await book.GetOrCreateAsync(characterId, ct);
        }
        catch (Exception ex)
        {
            onStorageError?.Invoke(MonsterCollectionStorageOp.BookLoad, characterId, ex);
            doc = new CollectionDocument { Id = characterId };
        }
        doc.ActivePetFlag = activePetFlag(doc);
        return doc;
    }

    private async Task SaveBookAsync(CollectionDocument doc, CancellationToken ct)
    {
        try
        {
            await book.SaveAsync(doc, ct);
        }
        catch (Exception ex)
        {
            onStorageError?.Invoke(MonsterCollectionStorageOp.BookSave, doc.Id, ex);
        }
    }

    private async Task<MonsterCollectionProgressDocument> LoadProgressAsync(uint characterId, CancellationToken ct)
    {
        try
        {
            return await progress.GetAsync(characterId, ct) ?? new MonsterCollectionProgressDocument { Id = characterId };
        }
        catch (Exception ex)
        {
            onStorageError?.Invoke(MonsterCollectionStorageOp.ProgressLoad, characterId, ex);
            return new MonsterCollectionProgressDocument { Id = characterId };
        }
    }

    private async Task SaveProgressAsync(MonsterCollectionProgressDocument doc, CancellationToken ct)
    {
        try
        {
            await progress.SaveAsync(doc, ct);
        }
        catch (Exception ex)
        {
            onStorageError?.Invoke(MonsterCollectionStorageOp.ProgressSave, doc.Id, ex);
        }
    }
}
