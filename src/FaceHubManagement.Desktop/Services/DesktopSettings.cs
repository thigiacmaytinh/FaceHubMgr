namespace FaceHubManagement.Desktop.Services;

public sealed class DesktopSettings
{
    public string Theme { get; set; } = "System";
    public int ResourceRefreshMilliseconds { get; set; } = 1000;
    public int ServiceRefreshMilliseconds { get; set; } = 2000;
    public int LogRefreshMilliseconds { get; set; } = 2000;
    public string WindowsPipeName { get; set; } = "FaceHubManagement.Agent";
    public string LinuxSocketPath { get; set; } = "/run/facehub-management/agent.sock";
}
