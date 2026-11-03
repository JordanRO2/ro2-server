namespace RO2.Socials.Domain;

/// <summary>One stored block-list entry: which character is blocked and the name it was stored with.</summary>
public sealed class BlockListEntry
{
    /// <summary>
    /// The blocked character's <c>Character.Id</c>, the key every Chat store uses. The field keeps its
    /// original name and 64-bit type so the rows written before the GID change still load: they already
    /// hold exactly this value, because the old code stored <c>target.Id</c> here and sent it as the wire
    /// uid. The wire uid is now the GID built from it (<see cref="CharacterGid"/>), so those rows need no
    /// migration, and <see cref="IBlockListRepository.AddAsync"/> keeps writing the bare id.
    /// </summary>
    public ulong Uid { get; set; }

    /// <summary>The character name as last resolved from the characters collection.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// One document per character holding its messenger block list. <see cref="Id"/> maps to Mongo
/// <c>_id</c> and is the owner's character id, the Id-&gt;_id convention KharaTraceDocument uses.
/// </summary>
public sealed class BlockListDocument
{
    /// <summary>Owner character id. Maps to Mongo _id.</summary>
    public uint Id { get; set; }

    /// <summary>Blocked characters in the order they were added (not sorted).</summary>
    public List<BlockListEntry> Entries { get; set; } = new();
}
