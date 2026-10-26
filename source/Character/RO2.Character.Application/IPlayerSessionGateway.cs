using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// A port onto one connected player's live session state, built per request by the host around its own
/// runtime player object (the <c>PlayerState : IWallet</c> pattern). Flows use it to mirror a change onto the
/// in-memory session before it is persisted, so other online reads see the new value immediately.
/// </summary>
/// <remarks>
/// The online <see cref="Snapshot"/> is the character entity the flow mutates directly, because it is a Domain
/// type; only the session's own fields (such as <see cref="BindIndex"/>) are forwarded through the port. The
/// surface grows one slice at a time as more character flows are carved out of the host.
/// </remarks>
public interface IPlayerSessionGateway
{
    /// <summary>The online character entity backing this session, or null when none is loaded.</summary>
    Character? Snapshot { get; }

    /// <summary>The session's respawn bind index (the save-point the player last chose).</summary>
    ushort BindIndex { get; set; }

    /// <summary>The session's character name (mirrored on a rename so live reads see the new name at once).</summary>
    string Name { get; set; }
}
