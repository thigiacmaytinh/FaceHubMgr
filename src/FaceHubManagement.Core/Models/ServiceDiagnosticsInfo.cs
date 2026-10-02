namespace FaceHubManagement.Core.Models;

public sealed class ServiceDiagnosticsInfo
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ServiceId { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string? Operation { get; set; }
    public string State { get; set; } = string.Empty;
    public string LikelyCause { get; set; } = string.Empty;
    public string ServiceStatus { get; set; } = string.Empty;
    public string ExitCode { get; set; } = string.Empty;
    public string ConfigurationTest { get; set; } = string.Empty;
    public string PortConflicts { get; set; } = string.Empty;
    public string RecentServiceLogs { get; set; } = string.Empty;
    public string ApplicationLogs { get; set; } = string.Empty;
}
