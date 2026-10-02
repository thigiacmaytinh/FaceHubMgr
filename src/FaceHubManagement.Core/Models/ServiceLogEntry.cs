namespace FaceHubManagement.Core.Models;

public sealed class ServiceLogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = "INFO";
    public string Component { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? ServiceId { get; set; }
    public string? Operation { get; set; }
}
