using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Extensions;

/// <summary>
/// Extension methods for Zone handler DI registration and wiring.
/// </summary>
public static class ZoneHandlerServiceExtensions
{
 /// <summary>
 /// Registers all Zone handlers from the specified assembly.
 /// </summary>
    public static IServiceCollection AddZoneHandlers(this IServiceCollection services, Assembly assembly)
    {
        
        var rmiHandlers = assembly.GetTypes()
            .Where(t => typeof(IZoneRmiHandler).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var handlerType in rmiHandlers)
        {
            services.AddSingleton(typeof(IZoneRmiHandler), handlerType);
        }

        
        var coreHandlers = assembly.GetTypes()
            .Where(t => typeof(IZoneCoreHandler).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var handlerType in coreHandlers)
        {
            services.AddSingleton(typeof(IZoneCoreHandler), handlerType);
        }

        
        var multiCoreHandlers = assembly.GetTypes()
            .Where(t => typeof(IZoneMultiCoreHandler).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var handlerType in multiCoreHandlers)
        {
            services.AddSingleton(typeof(IZoneMultiCoreHandler), handlerType);
        }

        return services;
    }

 /// <summary>
 /// Wires all registered Zone handlers to the dispatcher and server.
 /// </summary>
    public static void WireZoneHandlers(
        this IServiceProvider services,
        RmiDispatcher dispatcher,
        ProudServer server,
        ILogger logger)
    {
        
        var rmiHandlers = services.GetServices<IZoneRmiHandler>();
        var rmiCount = 0;

        foreach (var handler in rmiHandlers)
        {
            var h = handler;
            dispatcher.Register(handler.Opcode, (session, payload) => h.HandleAsync(session, payload), handler.Name);
            logger.LogDebug("Registered Zone RMI handler: {Name} (0x{Opcode:X4})", handler.Name, handler.Opcode);
            rmiCount++;
        }

        
        var coreHandlers = services.GetServices<IZoneCoreHandler>();
        var coreHandlerMap = new Dictionary<ProudCoreOpCode, IZoneCoreHandler>();

        foreach (var handler in coreHandlers)
        {
            coreHandlerMap[handler.Opcode] = handler;
            logger.LogDebug("Registered Zone Core handler: {Name} (0x{Opcode:X2})", handler.Name, (byte)handler.Opcode);
        }

        
        var multiCoreHandlers = services.GetServices<IZoneMultiCoreHandler>();
        var multiCoreHandlerMap = new Dictionary<ProudCoreOpCode, IZoneMultiCoreHandler>();

        foreach (var handler in multiCoreHandlers)
        {
            foreach (var opcode in handler.Opcodes)
            {
                multiCoreHandlerMap[opcode] = handler;
            }
            logger.LogDebug("Registered Zone Multi-Core handler: {Name} ({Opcodes})",
                handler.Name, string.Join(", ", handler.Opcodes.Select(o => $"0x{(byte)o:X2}")));
        }

        server.CoreMessageReceived += async (session, opcode, payload) =>
        {
            if (coreHandlerMap.TryGetValue(opcode, out var coreHandler))
            {
                await coreHandler.HandleAsync(session, payload);
                return;
            }
            if (multiCoreHandlerMap.TryGetValue(opcode, out var multiHandler))
            {
                await multiHandler.HandleAsync(session, payload);
                return;
            }
            logger.LogDebug("Session {Id}: Unhandled Zone core message 0x{Opcode:X2}",
                session.SessionId, (byte)opcode);
        };

        logger.LogInformation("Wired {RmiCount} Zone RMI, {CoreCount} Core, {MultiCount} Multi-Core handlers",
            rmiCount, coreHandlerMap.Count, multiCoreHandlerMap.Count);
    }

 /// <summary>
 /// Initializes the broadcast service with the server reference.
 /// </summary>
    public static void InitializeBroadcastService(this IServiceProvider services, ProudServer server)
    {
        var broadcastService = services.GetRequiredService<BroadcastService>();
        broadcastService.SetServer(server);
    }
}
