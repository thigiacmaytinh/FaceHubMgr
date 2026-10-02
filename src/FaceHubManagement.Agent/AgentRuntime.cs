using FaceHubManagement.Agent.Configuration;
using FaceHubManagement.Agent.Ipc;
using FaceHubManagement.Agent.Firewall;
using FaceHubManagement.Agent.Logging;
using FaceHubManagement.Agent.Resources;
using FaceHubManagement.Agent.Services;

namespace FaceHubManagement.Agent;

public sealed class AgentRuntime : IDisposable
{
    private readonly RollingFileLog _log;
    private readonly NvmlMonitor _nvml;
    private readonly WindowsGpuEngineMonitor? _windowsGpuEngines;
    private readonly IIpcServer _server;
    private readonly AgentRequestDispatcher _dispatcher;
    private readonly LinuxUfwFirewallManager? _linuxFirewallManager;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public AgentRuntime()
    {
        AgentConfiguration configuration = ConfigurationLoader.Load();
        string logDirectory = ResolveLogDirectory(configuration.Logs.Directory);
        _log = new RollingFileLog(logDirectory, configuration.Logs.MaxFileBytes, configuration.Logs.RetainedFiles);
        _nvml = new NvmlMonitor();

        IManagedServiceManager serviceManager;
        IResourceMonitor resourceMonitor;
        IFirewallManager firewallManager;
        string firewallStatePath = Path.Combine(ResolveStateDirectory(), "firewall-ports.json");
        var firewallStore = new FirewallPortStore(firewallStatePath, configuration.Ports);
        if (OperatingSystem.IsWindows())
        {
            serviceManager = new WindowsServiceManager(configuration.Services);
            _windowsGpuEngines = new WindowsGpuEngineMonitor();
            resourceMonitor = new WindowsResourceMonitor(_nvml, _windowsGpuEngines);
            firewallManager = new WindowsFirewallManager(firewallStore);
            _server = new WindowsNamedPipeServer(configuration.Ipc.WindowsPipeName);
        }
        else if (OperatingSystem.IsLinux())
        {
            serviceManager = new LinuxSystemdServiceManager(configuration.Services);
            resourceMonitor = new LinuxResourceMonitor(_nvml);
            _linuxFirewallManager = new LinuxUfwFirewallManager(firewallStore);
            firewallManager = _linuxFirewallManager;
            _server = new LinuxUnixSocketServer(configuration.Ipc.LinuxSocketPath);
        }
        else
        {
            throw new PlatformNotSupportedException("FaceHub Management Agent supports Windows and Linux.");
        }

        var portMonitor = new PortMonitor(configuration.Ports, configuration.Services);
        var serviceDiagnostics = new ServiceDiagnosticsCollector(serviceManager, configuration.Services);
        _dispatcher = new AgentRequestDispatcher(
            serviceManager, serviceDiagnostics, resourceMonitor, portMonitor, firewallManager, _log, _startedAt);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _log.InfoAsync(
            "Agent",
            $"FaceHub Management Agent started on {(OperatingSystem.IsWindows() ? "Windows" : "Linux")}.",
            cancellationToken).ConfigureAwait(false);

        if (_linuxFirewallManager is not null)
        {
            try
            {
                await _linuxFirewallManager.ReconcileManagedRuntimeOverridesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _log.WarningAsync(
                    "Firewall",
                    $"Unable to reconcile Linux firewall runtime state: {ex.Message}",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            await _server.RunAsync(_dispatcher.HandleClientAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _log.InfoAsync("Agent", "FaceHub Management Agent stopped.", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _windowsGpuEngines?.Dispose();
        _nvml.Dispose();
    }

    private static string ResolveLogDirectory(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredDirectory));
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FaceHub Management",
                "logs");
        }

        if (OperatingSystem.IsLinux())
        {
            const string systemPath = "/var/log/facehub-management";
            try
            {
                Directory.CreateDirectory(systemPath);
                return systemPath;
            }
            catch (UnauthorizedAccessException)
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".local", "state", "facehub-management", "logs");
            }
        }

        throw new PlatformNotSupportedException();
    }
    private static string ResolveStateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FaceHub Management");
        }

        if (OperatingSystem.IsLinux())
        {
            const string systemPath = "/var/lib/facehub-management";
            try
            {
                Directory.CreateDirectory(systemPath);
                return systemPath;
            }
            catch (UnauthorizedAccessException)
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".local", "state", "facehub-management");
            }
        }

        throw new PlatformNotSupportedException();
    }

}
