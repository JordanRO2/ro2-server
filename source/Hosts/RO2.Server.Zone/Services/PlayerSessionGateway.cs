using RO2.Characters.Application;
using RO2.Characters.Domain;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wraps one <see cref="PlayerState"/> as an <see cref="IPlayerSessionGateway"/>, built per request so character
/// flows can mirror a change onto the live session. The members forward one-to-one, so writes land in the same
/// order and against the same fields as the handlers used before the flows were carved out.
/// </summary>
internal sealed class PlayerSessionGateway(PlayerState player) : IPlayerSessionGateway
{
    public Character? Snapshot => player.Character;

    public ushort BindIndex
    {
        get => player.BindIndex;
        set => player.BindIndex = value;
    }

    public string Name
    {
        get => player.Name;
        set => player.Name = value;
    }

    public byte Gender
    {
        set => player.Gender = value;
    }
}
