namespace FaceHubManagement.Core.Models;

public sealed class ManagedServiceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ServiceState State { get; set; }
    public int? ProcessId { get; set; }
    public long? UptimeSeconds { get; set; }
    public IReadOnlyList<int> Ports { get; set; } = Array.Empty<int>();
    public string? Detail { get; set; }
}
