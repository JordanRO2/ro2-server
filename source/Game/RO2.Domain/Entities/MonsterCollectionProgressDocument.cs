namespace RO2.Domain.Entities;

/// <summary>
/// Per-character monster-collection progress, one Mongo document per character in its own
/// <c>collection_monster_progress</c> collection. It lives beside the collection book document rather
/// than inside it because the book's save is a whole-document ReplaceOne: any field the book class does
/// not declare would be erased by the next register/select.
/// A registered collection with no entry here is level 1 / exp 0 - nothing has advanced it yet; an
/// unregistered one is level 0 (see MonsterCollectionRules.State in the Zone host).
/// </summary>
public sealed class MonsterCollectionProgressDocument
{
 /// <summary>Character id. Maps to Mongo _id.</summary>
    public uint Id { get; set; }

 /// <summary>One entry per collection that has progressed at least once.</summary>
    public List<MonsterCollectionEntry> Entries { get; set; } = new();

 /// <summary>The stored entry for <paramref name="npcId"/>, or null when it never progressed.</summary>
    public MonsterCollectionEntry? Find(uint npcId) => Entries.FirstOrDefault(e => e.NpcId == npcId);
}
