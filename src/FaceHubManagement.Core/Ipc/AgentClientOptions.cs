namespace FaceHubManagement.Core.Ipc;

public sealed class AgentClientOptions
{
    public string WindowsPipeName { get; set; } = "FaceHubManagement.Agent";
    public string LinuxSocketPath { get; set; } = "/run/facehub-management/agent.sock";

    // Keep health/telemetry requests short so a single slow endpoint cannot make
    // the desktop appear frozen. Long-running control operations have their own
    // larger budgets below.
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan HealthRequestTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan TelemetryRequestTimeout { get; set; } = TimeSpan.FromSeconds(6);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ServiceActionTimeout { get; set; } = TimeSpan.FromSeconds(40);
    public TimeSpan FirewallRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
