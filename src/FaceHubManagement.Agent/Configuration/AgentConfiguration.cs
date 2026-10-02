namespace FaceHubManagement.Agent.Configuration;

public sealed class AgentConfiguration
{
    public List<ManagedServiceDefinition> Services { get; set; } = [];
    public List<MonitoredPortDefinition> Ports { get; set; } = MonitoredPortDefinition.CreateDefaults();
    public IpcConfiguration Ipc { get; set; } = new();
    public LogConfiguration Logs { get; set; } = new();
}

public sealed class ManagedServiceDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string WindowsServiceName { get; set; } = string.Empty;
    public string LinuxUnitName { get; set; } = string.Empty;
    public List<int> Ports { get; set; } = [];
}


public sealed class MonitoredPortDefinition
{
    public int Port { get; set; }
    public string Name { get; set; } = string.Empty;

    public static List<MonitoredPortDefinition> CreateDefaults() =>
    [
        new() { Port = 80, Name = "HTTP" },
        new() { Port = 443, Name = "HTTPS" },
        new() { Port = 8000, Name = "FaceHub" },
        new() { Port = 27017, Name = "MongoDB" }
    ];

}

public sealed class IpcConfiguration
{
    public string WindowsPipeName { get; set; } = "FaceHubManagement.Agent";
    public string LinuxSocketPath { get; set; } = "/run/facehub-management/agent.sock";
}

public sealed class LogConfiguration
{
    public string? Directory { get; set; }
    public int MaxFileBytes { get; set; } = 5 * 1024 * 1024;
    public int RetainedFiles { get; set; } = 5;
}
