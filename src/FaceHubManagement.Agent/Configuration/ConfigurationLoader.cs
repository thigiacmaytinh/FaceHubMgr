using System.Text.Json;
using FaceHubManagement.Core.Ipc;

namespace FaceHubManagement.Agent.Configuration;

public static class ConfigurationLoader
{
    public static AgentConfiguration Load()
    {
        foreach (string path in CandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            string json = File.ReadAllText(path);
            AgentConfiguration? configuration = JsonSerializer.Deserialize<AgentConfiguration>(json, JsonDefaults.Options);
            if (configuration is null)
            {
                throw new InvalidDataException($"Agent configuration '{path}' is empty or invalid.");
            }

            EnsureRequiredServices(configuration);
            Validate(configuration, path);
            return configuration;
        }

        throw new FileNotFoundException(
            "FaceHub Management agent configuration was not found. Install the packaged configuration or place agentsettings.json next to the agent executable.");
    }


    private static void EnsureRequiredServices(AgentConfiguration configuration)
    {
        if (configuration.Services.Any(service => service.Id.Equals("mongodb", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        configuration.Services.Add(new ManagedServiceDefinition
        {
            Id = "mongodb",
            DisplayName = "MongoDB",
            Description = "MongoDB database service",
            WindowsServiceName = "MongoDB",
            LinuxUnitName = "mongod.service",
            Ports = [27017]
        });
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("FACEHUB_MANAGEMENT_CONFIG");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return Path.GetFullPath(explicitPath);
        }

        if (OperatingSystem.IsWindows())
        {
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            yield return Path.Combine(common, "FaceHub Management", "agentsettings.json");
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/etc/facehub-management/agentsettings.json";
        }

        yield return Path.Combine(AppContext.BaseDirectory, "agentsettings.json");
    }

    private static void Validate(AgentConfiguration configuration, string sourcePath)
    {
        if (configuration.Services.Count == 0)
        {
            throw new InvalidDataException($"Configuration '{sourcePath}' does not define any managed services.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedServiceDefinition service in configuration.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Id) ||
                string.IsNullOrWhiteSpace(service.DisplayName) ||
                string.IsNullOrWhiteSpace(service.WindowsServiceName) ||
                string.IsNullOrWhiteSpace(service.LinuxUnitName))
            {
                throw new InvalidDataException($"Configuration '{sourcePath}' contains an incomplete service definition.");
            }

            if (!ids.Add(service.Id))
            {
                throw new InvalidDataException($"Configuration '{sourcePath}' contains duplicate service id '{service.Id}'.");
            }

            if (service.Ports.Any(port => port is < 1 or > 65535))
            {
                throw new InvalidDataException($"Service '{service.Id}' contains an invalid TCP port.");
            }
        }

        if (configuration.Ports.Count == 0)
        {
            configuration.Ports = MonitoredPortDefinition.CreateDefaults();
        }

        var monitoredPorts = new HashSet<int>();
        foreach (MonitoredPortDefinition port in configuration.Ports)
        {
            if (port.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(port.Name))
            {
                throw new InvalidDataException($"Configuration '{sourcePath}' contains an invalid monitored port definition.");
            }

            if (!monitoredPorts.Add(port.Port))
            {
                throw new InvalidDataException($"Configuration '{sourcePath}' contains duplicate monitored port '{port.Port}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(configuration.Ipc.WindowsPipeName) ||
            string.IsNullOrWhiteSpace(configuration.Ipc.LinuxSocketPath))
        {
            throw new InvalidDataException($"Configuration '{sourcePath}' contains an invalid IPC endpoint.");
        }

        configuration.Logs.MaxFileBytes = Math.Clamp(configuration.Logs.MaxFileBytes, 1024 * 1024, 100 * 1024 * 1024);
        configuration.Logs.RetainedFiles = Math.Clamp(configuration.Logs.RetainedFiles, 1, 20);
    }
}
