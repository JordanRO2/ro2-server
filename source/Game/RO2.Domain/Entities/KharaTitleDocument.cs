namespace RO2.Domain.Entities;

/// <summary>
/// A character's selected Khara titles and the two clocks retail's 1438/2582 answers depend on.
/// <see cref="Id"/> maps to Mongo <c>_id</c> and is the character id (one document per character).
/// </summary>
public sealed class KharaTitleDocument
{
 /// <summary>Character id. Maps to Mongo _id.</summary>
    public uint Id { get; set; }

 /// <summary>The Khara id worn in slot A (1438 u32@0, boards 1/3/4/5), 0 = none.</summary>
    public uint KharaA { get; set; }

 /// <summary>The Khara id worn in slot B (1438 u32@4, board 6), 0 = none.</summary>
    public uint KharaB { get; set; }

 /// <summary>When slot A's 120000 ms timer last started, as an instant on the character's play clock,
 /// null when it never ran.</summary>
    public DateTime? ATimerStartUtc { get; set; }

 /// <summary>When the last 1438 of this character succeeded, as an instant on its play clock, null when none did.</summary>
    public DateTime? LastSuccessUtc { get; set; }

 /// <summary>Where the character's play clock stood when it was last stored: at each exit from the world
 /// and with every accepted select. Null in documents written before the play clock, whose instants are
 /// wall-clock ones.</summary>
    public DateTime? PlayClockUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
