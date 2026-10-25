using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Runs the countdown of timed enchants. Every tick, each worn item of a player in the world with seconds left loses that amount.
/// A player whose enchant expires receives a refreshed 2498 OnPcTrait without the bonus.
/// </summary>
/// <remarks>
/// Only worn items of players in this zone are ticked. Each update is a compare-and-set on the item's id, enchant id and seconds.
/// Failures are isolated per player and logged, so one failed session does not stop the others.
/// </remarks>
public sealed class TimedEnchantService : BackgroundService
{
    private readonly PlayerStateManager _players;
    private readonly BroadcastService _broadcast;
    private readonly EffectService _effects;
    private readonly GameDataStore _data;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TimedEnchantService> _logger;

    
    
    public TimedEnchantService(PlayerStateManager players, BroadcastService broadcast, EffectService effects,
        GameDataStore data, IServiceScopeFactory scopes, ILogger<TimedEnchantService> logger)
    {
        _players = players;
        _broadcast = broadcast;
        _effects = effects;
        _data = data;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Timed enchant service started (every {Sec}s)", TimedEnchant.TickSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TimedEnchant.TickSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Timed enchant tick failed; continuing with the next tick");
            }
        }
    }

 /// <summary>
 /// One tick over every player in the world. One DI scope serves the whole tick.
 /// </summary>
    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var enchants = scope.ServiceProvider.GetRequiredService<ITimedEnchantRepository>();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
        foreach (var player in _players.GetAllPlayers())
        {
            if (player.Character is not { } character || player.CharacterId == 0) continue;
            try
            {
                await TickPlayerAsync(player, character, enchants, inventory, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Timed enchant tick failed for session {Id}", player.SessionId);
            }
        }
    }

    private async Task TickPlayerAsync(PlayerState player, Character character,
        ITimedEnchantRepository enchants, IInventoryRepository inventory, CancellationToken ct)
    {
        uint characterId = player.CharacterId;
        var running = await enchants.GetRunningWornAsync(characterId, ct);
        if (running.Count == 0) return;

        var ranOut = new List<ushort>();
        foreach (var it in running)
        {
            ushort after = TimedEnchant.AfterTick(it.Seconds);
            if (await enchants.TryAdvanceTimerAsync(it.ItemId, it.EnchantId, it.Seconds, after, ct) && after == 0)
                ranOut.Add(it.EnchantId);
        }
        if (ranOut.Count == 0) return;

        _logger.LogInformation("{Name}: timed enchant {Ids} ran out", player.Name, string.Join(", ", ranOut));
        if (!player.MapLoaded) return;
        var session = _broadcast.GetSession(player.SessionId);
        if (session is null) return;

        IReadOnlyList<InventoryItem> equipped = await inventory.GetEquipmentAsync(characterId);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, _effects.RefreshPcTraitSheet(player, character, equipped));
        
        (player.WeaponMinDamage, player.WeaponMaxDamage, player.AutoAttackRange) = StatService.WeaponDamageRange(_data, equipped);
    }
}
