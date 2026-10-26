using RO2.Characters.Application;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="IOnlineCharacterView"/> with the live <see cref="PlayerStateManager"/>.</summary>
internal sealed class OnlineCharacterView(PlayerStateManager players) : IOnlineCharacterView
{
    public bool IsReviewAllowed(uint characterId) =>
        players.GetPlayerByCharacterId(characterId)?.Character?.AllowAnyoneReview ?? true;
}
