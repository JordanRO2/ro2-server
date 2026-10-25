using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Write-behind persistence for high-churn player state (position, vitals, experience, level and wallet).
/// Changed players are saved in the background, so no periodic save window exists.
/// </summary>
public sealed class PersistenceService : BackgroundService, IDirtySink
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersistenceService> _logger;

    
    
    private readonly ConcurrentDictionary<uint, PlayerState> _dirty = new();

    
    
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    
    
    
    
    
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public PersistenceService(IServiceScopeFactory scopeFactory, ILogger<PersistenceService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

 /// <inheritdoc />
    public void Mark(PlayerState player)
    {
        if (player.CharacterId == 0) return;   
        _dirty[player.CharacterId] = player;
        _wake.Writer.TryWrite(0);              
    }

 /// <inheritdoc />
    public void Forget(uint characterId) => _dirty.TryRemove(characterId, out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Persistence service started (real-time write-behind, coalescing — no periodic save)");

        try
        {
            while (await _wake.Reader.WaitToReadAsync(stoppingToken))
            {
                
                
                _wake.Reader.TryRead(out _);
                await DrainAsync();
            }
        }
        catch (OperationCanceledException)
        {
            
        }

        
        
        for (int i = 0; i < 10 && !_dirty.IsEmpty; i++)
            await DrainAsync();
    }

    private async Task DrainAsync()
    {
        if (_dirty.IsEmpty) return;

        
        
        await _writeGate.WaitAsync();
        try
        {
            var batch = new List<PlayerStateSave>(_dirty.Count);
            var taken = new List<PlayerState>(_dirty.Count);
            foreach (var id in _dirty.Keys.ToArray())
            {
                if (_dirty.TryRemove(id, out var p) && p.CharacterId != 0)
                {
                    batch.Add(Snapshot(p));
                    taken.Add(p);
                }
            }
            if (batch.Count == 0) return;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var characters = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();
                await characters.BulkSavePlayerStateAsync(batch);
                if (batch.Count > 1)
                    _logger.LogDebug("Persisted {N} changed players in real time", batch.Count);
            }
            catch (Exception ex)
            {
                
                _logger.LogWarning(ex, "Persistence drain failed for {N} player(s) — re-queued", taken.Count);
                foreach (var p in taken) _dirty.TryAdd(p.CharacterId, p);
                _wake.Writer.TryWrite(0);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

 /// <summary>
 /// Persists one player's latest state immediately and clears its dirty mark. Used at hard boundaries such as
 /// logout, channel change and cross-map warp.
 /// </summary>
    public async Task FlushAsync(PlayerState player)
    {
        if (player.CharacterId == 0) { _dirty.TryRemove(player.CharacterId, out _); return; }

        await _writeGate.WaitAsync();
        try
        {
            _dirty.TryRemove(player.CharacterId, out _);
            var save = Snapshot(player);   
            using var scope = _scopeFactory.CreateScope();
            var characters = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();
            await characters.BulkSavePlayerStateAsync(new[] { save });
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static PlayerStateSave Snapshot(PlayerState p) =>
        new(p.CharacterId, p.MapId, p.X, p.Y, p.Z, p.Hp, p.MaxHp, p.Mp, p.MaxMp, p.Level, p.Exp, p.Gold, p.BankGold, p.Job, p.BindIndex,
            p.FacingYaw);
}
