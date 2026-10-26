using RO2.Characters.Application;
using RO2.Server.Zone.Handlers.Rmi;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="IGuildMembershipView"/> with the live <see cref="GuildService"/>.</summary>
internal sealed class GuildMembershipView(IGuildStore store) : IGuildMembershipView
{
    public async Task<bool> IsGuildMasterAsync(uint characterId)
    {
        await GuildService.Instance.InitAsync(store);
        return ReserveDeleteHandler.IsGuildMaster(GuildService.Instance.GetByCharId(characterId), characterId);
    }
}
