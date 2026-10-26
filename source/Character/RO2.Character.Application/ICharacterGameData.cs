namespace RO2.Characters.Application;

/// <summary>
/// Read-only access to the static game-data tables a character flow needs, so the flows do not depend on the
/// host's data-store type. The host backs it with its loaded tables. The surface grows one slice at a time.
/// </summary>
public interface ICharacterGameData
{
    /// <summary>True when <paramref name="unionId"/> is a union defined in Union_Info.</summary>
    bool UnionExists(uint unionId);
}
