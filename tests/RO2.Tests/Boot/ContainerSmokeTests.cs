using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Handlers;
using RO2.Network.ProudNet;
using Xunit;
using Xunit.Abstractions;
using ChatConfig = RO2.Server.Chat.Configuration.ServerConfiguration;
using ChatProgram = RO2.Server.Chat.Program;
using LoginConfig = RO2.Server.Login.Configuration.ServerConfiguration;
using LoginProgram = RO2.Server.Login.Program;
using ZoneConfig = RO2.Server.Zone.Configuration.ServerConfiguration;
using ZoneProgram = RO2.Server.Zone.Program;
using ZoneServices = RO2.Server.Zone.Services;

namespace RO2.Tests.Boot;

/// <summary>
/// Boot smoke tests for the three server hosts. Each test builds the host's real container and checks that its services, handlers and service lookups resolve, without opening any network connection.
/// </summary>
/// <remarks>
/// Checks that the container builds with validation on, that every handler and hosted service resolves, that handler registration has no duplicate opcodes, and that every service looked up by the host source resolves from a scope.
/// </remarks>
public class ContainerSmokeTests(ITestOutputHelper output)
{
    
    
    
    
    
    private const string TestMongo = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100&connectTimeoutMS=100";

    private void Report(string host, BootResult r) =>
        output.WriteLine($"{host}: {r.RmiHandlers} RMI handlers, {r.CoreHandlers} core, {r.MultiCoreHandlers} multi-core, {r.HostedServices} hosted services, {r.Tickables} ITickable, {r.Dispatcher.GetRegisteredOpcodes().Count()} opcodes on the dispatcher, {r.Services.Count} registrations");

    private sealed record BootResult(
        ServiceProvider Provider,
        IServiceCollection Services,
        RmiDispatcher Dispatcher,
        int RmiHandlers,
        int CoreHandlers,
        int MultiCoreHandlers,
        int HostedServices,
        int Tickables);

    [Fact]
    public void Login_container_builds_and_every_handler_and_service_resolves()
    {
        var config = new LoginConfig();
        config.Database.ConnectionString = TestMongo;
        var r = Boot<ILoginRmiHandler, ILoginCoreHandler, ILoginMultiCoreHandler>(
            s => LoginProgram.ConfigureServices(s, config),
            (sp, d, server, log) => sp.WireLoginHandlers(d, server, log));

        Report("Login", r);
        AssertServiceLocatorTargetsResolve(r.Provider, typeof(LoginProgram).Assembly, "RO2.Server.Login");
        Assert.True(r.RmiHandlers > 0);
        Assert.Equal(r.RmiHandlers, r.Dispatcher.GetRegisteredOpcodes().Count());
    }

    [Fact]
    public void Chat_container_builds_and_every_handler_and_service_resolves()
    {
        var config = new ChatConfig();
        config.Database.ConnectionString = TestMongo;
        var r = Boot<IChatRmiHandler, IChatCoreHandler, IChatMultiCoreHandler>(
            s => ChatProgram.ConfigureServices(s, config),
            (sp, d, server, log) => sp.WireChatHandlers(d, server, log));

        Report("Chat", r);
        AssertServiceLocatorTargetsResolve(r.Provider, typeof(ChatProgram).Assembly, "RO2.Server.Chat");
        Assert.True(r.RmiHandlers > 0);
        Assert.Equal(r.RmiHandlers, r.Dispatcher.GetRegisteredOpcodes().Count());
    }

    [Fact]
    public void Zone_container_builds_and_every_handler_and_service_resolves()
    {
        
        ZoneServices.DyeColorBson.Register();

        var config = new ZoneConfig();
        config.Database.ConnectionString = TestMongo;
        var r = Boot<IZoneRmiHandler, IZoneCoreHandler, IZoneMultiCoreHandler>(
            s => ZoneProgram.ConfigureServices(s, config),
            (sp, d, server, log) =>
            {
                
                RO2.Server.Zone.Extensions.ZoneHandlerServiceExtensions.InitializeBroadcastService(sp, server);
                RO2.Server.Zone.Extensions.ZoneHandlerServiceExtensions.WireZoneHandlers(sp, d, server, log);
            });

        Report("Zone", r);
        AssertServiceLocatorTargetsResolve(r.Provider, typeof(ZoneProgram).Assembly, "RO2.Server.Zone");
        Assert.True(r.RmiHandlers > 0);
        Assert.True(r.HostedServices > 0);
        Assert.True(r.Tickables > 0);
        Assert.Equal(r.RmiHandlers, r.Dispatcher.GetRegisteredOpcodes().Count());
    }

    private static BootResult Boot<TRmi, TCore, TMulti>(
        Action<IServiceCollection> configure,
        Action<IServiceProvider, RmiDispatcher, ProudServer, ILogger> wire)
        where TRmi : class, IRmiHandler
        where TCore : class, ICoreHandler
        where TMulti : class, IMultiCoreHandler
    {
        var services = new ServiceCollection();
        
        
        services.AddLogging();
        
        services.AddSingleton<IHostApplicationLifetime, StubLifetime>();
        configure(services);

        
        
        ServiceProvider provider;
        try
        {
            provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }
        catch (AggregateException ex)
        {
            throw new Xunit.Sdk.XunitException("DI container failed validation:\n" + Describe(ex));
        }

        
        var rmi = ResolveAll<TRmi>(provider, services);
        var core = ResolveAll<TCore>(provider, services);
        var multi = ResolveAll<TMulti>(provider, services);

        
        var hosted = ResolveAll<IHostedService>(provider, services);
        var tickables = ResolveAll<ZoneServices.ITickable>(provider, services);

        
        
        var failures = new List<string>();
        using (var scope = provider.CreateScope())
        {
            foreach (var d in services.Where(d => !d.ServiceType.IsGenericTypeDefinition).DistinctBy(d => d.ServiceType))
            {
                try { scope.ServiceProvider.GetServices(d.ServiceType).Count(); }
                catch (Exception ex) { failures.Add($"{d.ServiceType.FullName}: {Root(ex).Message}"); }
            }
        }
        Assert.True(failures.Count == 0, "Registered services that do not resolve:\n" + string.Join("\n", failures));

        
        var server = new ProudServer("Smoke", NullLogger.Instance);
        var dispatcher = new RmiDispatcher(NullLogger.Instance);
        wire(provider, dispatcher, server, NullLogger.Instance);
        foreach (var h in rmi)
            Assert.True(dispatcher.HasHandler(h.Opcode), $"{h.GetType().Name} (opcode {h.Opcode}) is not on the dispatcher");
        Assert.Equal(rmi.Count, rmi.Select(h => h.Opcode).Distinct().Count());

        return new BootResult(provider, services, dispatcher, rmi.Count, core.Count, multi.Count, hosted.Count, tickables.Count);
    }

 /// <summary>
 /// Resolves every registration of a service type. A failure names every implementation that cannot be resolved, with its missing dependency.
 /// </summary>
    private static List<T> ResolveAll<T>(IServiceProvider provider, IServiceCollection services) where T : class
    {
        try
        {
            return provider.GetServices<T>().ToList();
        }
        catch (Exception)
        {
            var bad = new List<string>();
            foreach (var d in services.Where(d => d.ServiceType == typeof(T)))
            {
                try
                {
                    if (d.ImplementationType is { } impl)
                        ActivatorUtilities.CreateInstance(provider, impl);
                    else
                        provider.GetServices<T>().ToList();
                }
                catch (Exception ex)
                {
                    bad.Add($"{d.ImplementationType?.FullName ?? d.ServiceType.FullName}: {Root(ex).Message}");
                }
            }
            throw new Xunit.Sdk.XunitException($"Resolving {typeof(T).Name} failed:\n" + string.Join("\n", bad));
        }
    }

    private static readonly Regex LocatorCall =
        new(@"(?<![A-Za-z0-9_])Get(?:Required)?Service<(?<t>[A-Za-z_][A-Za-z0-9_.]*)>[ ]*[(]", RegexOptions.Compiled);

 /// <summary>
 /// Checks that every service type the host source requests from the container resolves from a scope. Handlers run in a per-message scope, so this catches a missing registration before the first packet arrives.
 /// </summary>
    private static void AssertServiceLocatorTargetsResolve(IServiceProvider root, Assembly host, string projectDir)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RO2_Server.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "RO2_Server.sln not found above the test output directory");
        // Host projects live under source/Hosts/<project>; fall back to a scoped search so a later
        // layout change does not silently break this scan.
        var src = Path.Combine(dir!, "source", "Hosts", projectDir);
        if (!Directory.Exists(src))
            src = Directory.EnumerateDirectories(Path.Combine(dir!, "source"), projectDir, SearchOption.AllDirectories)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, projectDir + ".csproj"))) ?? src;

        var candidates = new List<Type>(host.GetTypes());
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var n = asm.GetName().Name ?? "";
            if (asm == host || !(n.StartsWith("RO2.", StringComparison.Ordinal) || n.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)))
                continue;
            try { candidates.AddRange(asm.GetExportedTypes()); } catch (NotSupportedException) { }
        }

        var targets = new SortedDictionary<string, List<string>>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (var line in File.ReadLines(file))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match m in LocatorCall.Matches(line))
                {
                    var name = m.Groups["t"].Value;
                    if (!targets.TryGetValue(name, out var files)) targets[name] = files = [];
                    files.Add(Path.GetFileName(file));
                }
            }
        }
        Assert.True(targets.Count > 5, $"expected to find service-locator calls under {src}, found {targets.Count}");

        var problems = new List<string>();
        using var scope = root.CreateScope();
        foreach (var (name, files) in targets)
        {
            var simple = name[(name.LastIndexOf('.') + 1)..];
            var matches = candidates
                .Where(t => t.Name == simple && (name == simple || (t.FullName ?? "").EndsWith(name, StringComparison.Ordinal)))
                .ToList();
            var type = matches.FirstOrDefault(t => t.Assembly == host) ?? matches.FirstOrDefault();
            if (type is null) { problems.Add($"{name}: no such type found (used in {files[0]})"); continue; }
            try
            {
                if (scope.ServiceProvider.GetService(type) is null)
                    problems.Add($"{type.FullName}: not registered (asked for in {string.Join(", ", files.Distinct().Take(3))})");
            }
            catch (Exception ex) { problems.Add($"{type.FullName}: {Root(ex).Message}"); }
        }
        Assert.True(problems.Count == 0, "Service-locator targets that do not resolve:\n" + string.Join("\n", problems));
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static Exception Root(Exception ex) => ex.InnerException is null ? ex : Root(ex.InnerException);

    private static string Describe(AggregateException ex) =>
        string.Join("\n", ex.InnerExceptions.Select(e => "  - " + e.Message));
}
