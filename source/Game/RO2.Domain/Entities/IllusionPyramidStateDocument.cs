namespace RO2.Domain.Entities;

/// <summary>
/// A character's Illusion Pyramid state: its Mystical and Bright Phantom Stone balances and its top record, the tail
/// 2906 AnsIllusionPyramidGetMyState carries at +23 / +25 / +31. <see cref="Id"/> maps to Mongo <c>_id</c> and is the
/// character id.
/// </summary>
/// <remarks>
/// The balances are not bag items: the 2906 state reports them while the 2310 inventory holds
/// no 30000267 or 30000268 record, and the UI calls them "hidden items". The server keeps them itself, taking
/// a run's entry cost from them and paying mileage results into them (the Zone IllusionPyramidService). The
/// top record is the highest floor whose result the character fetched: every rise of 2906 +31
/// follows a 2908 of that floor, before any 1619.
/// </remarks>
public sealed class IllusionPyramidStateDocument
{
    public uint Id { get; set; }
    public int Mystical { get; set; }
    public int Bright { get; set; }
    public byte TopRecord { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
