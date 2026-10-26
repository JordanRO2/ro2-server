namespace RO2.Characters.Application;

/// <summary>
/// A read-only view of an online character's own settings, enough for the flows that must respect them. The host
/// backs it with its live player registry. Replaces the former static review-permission lookup.
/// </summary>
public interface IOnlineCharacterView
{
    /// <summary>Whether the character lets others review it; the default is true when it is not online.</summary>
    bool IsReviewAllowed(uint characterId);
}
