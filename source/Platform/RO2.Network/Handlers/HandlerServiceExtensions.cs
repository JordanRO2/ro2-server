using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;

namespace RO2.Network.Handlers;

/// <summary>
/// Extension methods for registering packet handlers via DI
/// </summary>
public static class HandlerServiceExtensions
{
 /// <summary>
 /// Register all Login server handlers from the specified assembly.
 /// Scans for types implementing ILoginRmiHandler, ILoginCoreHandler, and ILoginMultiCoreHandler.
 /// </summary>
 /// <param name="services">Service collection</param>
 /// <param name="assembly">Assembly to scan for handlers</param>
    public static IServiceCollection AddLoginHandlers(this IServiceCollection services, Assembly assembly)
    {
        
        var rmiHandlers = assembly.GetTypes()
            .Where(t => typeof(ILoginRmiHandler).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsInterface);

        foreach (var handlerType in rmiHandlers)
        {
            services.AddSingleton(typeof(ILoginRmiHandler), handlerType);
        }

        
        var coreHandlers = assembly.GetTypes()
            .Where(t => typeof(ILoginCoreHandler).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsInterface);

        foreach (var handlerType in coreHandlers)
        {
            services.AddSingleton(typeof(ILoginCoreHandler), handlerType);
        }

        
        var multiCoreHandlers = assembly.GetTypes()
            .Where(t => typeof(ILoginMultiCoreHandler).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsInterface);

        foreach (var handlerType in multiCoreHandlers)
        {
            services.AddSingleton(typeof(ILoginMultiCoreHandler), handlerType);
        }

        return services;
    }

 /// <summary>
 /// Register all Zone server handlers from the specified assembly.
 /// Scans for types implementing IZoneRmiHandler, IZoneCoreHandler, and IZoneMultiCoreHandler.
 /// </summary>
 /// <param name="services">Service collection</param>
 /// <param name="assembly">Assembly to scan for handlers</param>
    public static IServiceCollection AddZoneHandlers(this IServiceCollection services, Assembly assembly)
    {
        
        var rmiHandlers = assembly.GetTypes()
            .Where(t => typeof(IZoneRmiHandler).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsInterface);

        foreach (var handlerType in rmiHandlers)
        {
            services.AddSingleton(typeof(IZoneRmiHandler), handlerType);
        }

        
        var coreHandlers = assembly.GetTypes()
            .Where(t => typeof(IZoneCoreHandler).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsInterface);

        foreach (var handlerType in coreHandlers)
        {
            services.AddSingleton(typeof(IZoneCoreHandler), handlerType);
        }

        
        var multiCoreHandlers = assembly.GetTypes()
            .Where(t => typeof(IZoneMultiCoreHandler).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsInterface);

        foreach (var handlerType in multiCoreHandlers)
        {
            services.AddSingleton(typeof(IZoneMultiCoreHandler), handlerType);
        }

        return services;
    }

 /// <summary>
 /// Wire up Login server handlers to the RMI dispatcher and core message handler.
 /// </summary>
 /// <param name="services">Service provider</param>
 /// <param name="dispatcher">RMI dispatcher</param>
 /// <param name="server">ProudNet server for core message handling</param>
 /// <param name="logger">Logger for handler registration</param>
    public static void WireLoginHandlers(
        this IServiceProvider services,
        RmiDispatcher dispatcher,
        ProudServer server,
        ILogger logger)
    {
        
        var rmiHandlers = services.GetServices<ILoginRmiHandler>();
        foreach (var handler in rmiHandlers)
        {
            
            var h = handler;
            dispatcher.Register(handler.Opcode, (session, payload) => h.HandleAsync(session, payload), handler.Name);
            logger.LogDebug("Registered Login RMI handler: {Name} (0x{Opcode:X4})", handler.Name, handler.Opcode);
        }

        
        var coreHandlerMap = new Dictionary<ProudCoreOpCode, ICoreHandler>();
        var coreHandlers = services.GetServices<ILoginCoreHandler>();
        foreach (var handler in coreHandlers)
        {
            coreHandlerMap[handler.Opcode] = handler;
            logger.LogDebug("Registered Login Core handler: {Name} (0x{Opcode:X2})", handler.Name, (byte)handler.Opcode);
        }

        
        var multiCoreHandlerMap = new Dictionary<ProudCoreOpCode, IMultiCoreHandler>();
        var multiCoreHandlers = services.GetServices<ILoginMultiCoreHandler>();
        foreach (var handler in multiCoreHandlers)
        {
            foreach (var opcode in handler.Opcodes)
            {
                multiCoreHandlerMap[opcode] = handler;
                logger.LogDebug("Registered Login Multi-Core handler: {Name} (0x{Opcode:X2})", handler.Name, (byte)opcode);
            }
        }

        
        server.CoreMessageReceived += async (session, opcode, payload) =>
        {
            if (coreHandlerMap.TryGetValue(opcode, out var handler))
            {
                await handler.HandleAsync(session, payload);
            }
            else if (multiCoreHandlerMap.TryGetValue(opcode, out var multiHandler))
            {
                await multiHandler.HandleAsync(session, payload);
            }
            else
            {
                logger.LogWarning("Session {SessionId}: No handler for core opcode 0x{Opcode:X2}",
                    session.SessionId, (byte)opcode);
            }
        };
    }

 /// <summary>
 /// Wire up Zone server handlers to the RMI dispatcher and core message handler.
 /// </summary>
 /// <param name="services">Service provider</param>
 /// <param name="dispatcher">RMI dispatcher</param>
 /// <param name="server">ProudNet server for core message handling</param>
 /// <param name="logger">Logger for handler registration</param>
    public static void WireZoneHandlers(
        this IServiceProvider services,
        RmiDispatcher dispatcher,
        ProudServer server,
        ILogger logger)
    {
        
        var rmiHandlers = services.GetServices<IZoneRmiHandler>();
        foreach (var handler in rmiHandlers)
        {
            
            var h = handler;
            dispatcher.Register(handler.Opcode, (session, payload) => h.HandleAsync(session, payload), handler.Name);
            logger.LogDebug("Registered Zone RMI handler: {Name} (0x{Opcode:X4})", handler.Name, handler.Opcode);
        }

        
        var coreHandlerMap = new Dictionary<ProudCoreOpCode, ICoreHandler>();
        var coreHandlers = services.GetServices<IZoneCoreHandler>();
        foreach (var handler in coreHandlers)
        {
            coreHandlerMap[handler.Opcode] = handler;
            logger.LogDebug("Registered Zone Core handler: {Name} (0x{Opcode:X2})", handler.Name, (byte)handler.Opcode);
        }

        
        var multiCoreHandlerMap = new Dictionary<ProudCoreOpCode, IMultiCoreHandler>();
        var multiCoreHandlers = services.GetServices<IZoneMultiCoreHandler>();
        foreach (var handler in multiCoreHandlers)
        {
            foreach (var opcode in handler.Opcodes)
            {
                multiCoreHandlerMap[opcode] = handler;
                logger.LogDebug("Registered Zone Multi-Core handler: {Name} (0x{Opcode:X2})", handler.Name, (byte)opcode);
            }
        }

        
        server.CoreMessageReceived += async (session, opcode, payload) =>
        {
            if (coreHandlerMap.TryGetValue(opcode, out var handler))
            {
                await handler.HandleAsync(session, payload);
            }
            else if (multiCoreHandlerMap.TryGetValue(opcode, out var multiHandler))
            {
                await multiHandler.HandleAsync(session, payload);
            }
            else
            {
                logger.LogWarning("Session {SessionId}: No handler for core opcode 0x{Opcode:X2}",
                    session.SessionId, (byte)opcode);
            }
        };
    }

 /// <summary>
 /// Register all Chat server handlers from the specified assembly.
 /// </summary>
    public static IServiceCollection AddChatHandlers(this IServiceCollection services, Assembly assembly)
    {
        var rmiHandlers = assembly.GetTypes()
            .Where(t => typeof(IChatRmiHandler).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsInterface);

        foreach (var handlerType in rmiHandlers)
            services.AddSingleton(typeof(IChatRmiHandler), handlerType);

        var coreHandlers = assembly.GetTypes()
            .Where(t => typeof(IChatCoreHandler).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsInterface);

        foreach (var handlerType in coreHandlers)
            services.AddSingleton(typeof(IChatCoreHandler), handlerType);

        var multiCoreHandlers = assembly.GetTypes()
            .Where(t => typeof(IChatMultiCoreHandler).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsInterface);

        foreach (var handlerType in multiCoreHandlers)
            services.AddSingleton(typeof(IChatMultiCoreHandler), handlerType);

        return services;
    }

 /// <summary>
 /// Wire all Chat server handlers to the dispatcher and ProudNet server.
 /// </summary>
    public static void WireChatHandlers(
        this IServiceProvider services,
        RmiDispatcher dispatcher,
        ProudServer server,
        ILogger logger)
    {
        var rmiHandlers = services.GetServices<IChatRmiHandler>();
        foreach (var handler in rmiHandlers)
        {
            var h = handler;
            dispatcher.Register(handler.Opcode, (session, payload) => h.HandleAsync(session, payload), handler.Name);
            logger.LogDebug("Registered Chat RMI handler: {Name} (0x{Opcode:X4})", handler.Name, handler.Opcode);
        }

        var coreHandlerMap = new Dictionary<ProudCoreOpCode, ICoreHandler>();
        foreach (var handler in services.GetServices<IChatCoreHandler>())
        {
            coreHandlerMap[handler.Opcode] = handler;
            logger.LogDebug("Registered Chat Core handler: {Name} (0x{Opcode:X2})", handler.Name, (byte)handler.Opcode);
        }

        var multiCoreHandlerMap = new Dictionary<ProudCoreOpCode, IMultiCoreHandler>();
        foreach (var handler in services.GetServices<IChatMultiCoreHandler>())
        {
            foreach (var opcode in handler.Opcodes)
            {
                multiCoreHandlerMap[opcode] = handler;
                logger.LogDebug("Registered Chat Multi-Core handler: {Name} (0x{Opcode:X2})", handler.Name, (byte)opcode);
            }
        }

        var rmiCount = rmiHandlers.Count();
        var coreCount = coreHandlerMap.Count;
        var multiCount = multiCoreHandlerMap.Count;
        logger.LogInformation("Wired {Rmi} Chat RMI, {Core} Core, {Multi} Multi-Core handlers",
            rmiCount, coreCount, multiCount);

        server.CoreMessageReceived += async (session, opcode, payload) =>
        {
            if (coreHandlerMap.TryGetValue(opcode, out var handler))
            {
                await handler.HandleAsync(session, payload);
            }
            else if (multiCoreHandlerMap.TryGetValue(opcode, out var multiHandler))
            {
                await multiHandler.HandleAsync(session, payload);
            }
            else
            {
                logger.LogWarning("Session {SessionId}: No handler for core opcode 0x{Opcode:X2}",
                    session.SessionId, (byte)opcode);
            }
        };
    }
}
