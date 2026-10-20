namespace RO2.Domain.Entities;

/// <summary>
/// One accepted, not yet completed Khara of a character: the element of the 2310 accepted-Khara vector.
/// </summary>
public sealed class KharaActiveDocument
{
 /// <summary>2310 accepted-record state for an accepted Khara (see <see cref="State"/>). The Zone
 /// KharaService re-exposes it as <c>KharaService.StateInProgress</c>.</summary>
    public const uint StateInProgress = 2;

    public uint KharaId { get; set; }

 /// <summary>
 /// The u32 after the id in the 2310 record. 2 for an accepted Khara; 3 for a Khara with a Fail_Type
 /// (380-393, 353), which this server does not model. Accepting always writes 2.
 /// </summary>
    public uint State { get; set; } = StateInProgress;

 /// <summary>
 /// One progress count per condition row (the Khara catalog's condition rows), in row order: the u16 the
 /// 2577 carries for that row and the u32 the 2310 record repeats for it.
 /// </summary>
    public List<uint> Counts { get; set; } = new();
}

/// <summary>
/// A character's Khara state: point balance, accepted Kharas with their per-row progress, and the ids of
/// the Kharas it completed. <see cref="Id"/> maps to Mongo <c>_id</c> and is the character id, the same
/// one-document-per-character convention the KeyConfig and Khara-trace stores use.
/// </summary>
/// <remarks>
/// WHY THE SERVER KEEPS IT. Retail carries all three at world entry, in the 2310 OnCharacterDetailACK:
/// the point balance at head+126, the accepted list and the completed list as the two vectors after the
/// quest vector. Both lists are sorted ascending by id and disjoint, which is how
/// the Zone KharaService keeps them.
/// </remarks>
public sealed class KharaStateDocument
{
 /// <summary>Character id. Maps to Mongo _id (one document per character).</summary>
    public uint Id { get; set; }

 /// <summary>Khara point balance (2310 head+126, 2502 u32@0).</summary>
    public uint Points { get; set; }

 /// <summary>Accepted Kharas, sorted by id.</summary>
    public List<KharaActiveDocument> Active { get; set; } = new();

 /// <summary>Completed Khara ids, sorted ascending.</summary>
    public List<uint> Completed { get; set; } = new();

    public DateTime UpdatedUtc { get; set; }

 /// <summary>A deep copy. The Zone KharaService never mutates a published document: it changes a copy and
 /// publishes that, so a reader (the 2310 writer) always sees one consistent state.</summary>
    public KharaStateDocument Clone() => new()
    {
        Id = Id,
        Points = Points,
        Active = Active.Select(a => new KharaActiveDocument
        {
            KharaId = a.KharaId,
            State = a.State,
            Counts = new List<uint>(a.Counts),
        }).ToList(),
        Completed = new List<uint>(Completed),
        UpdatedUtc = UpdatedUtc,
    };

    public KharaActiveDocument? FindActive(uint kharaId)
    {
        foreach (var a in Active)
            if (a.KharaId == kharaId) return a;
        return null;
    }

    public bool IsCompleted(uint kharaId) => Completed.BinarySearch(kharaId) >= 0;
}
