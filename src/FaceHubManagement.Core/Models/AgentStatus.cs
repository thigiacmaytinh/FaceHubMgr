namespace FaceHubManagement.Core.Models;

public sealed class AgentStatus
{
    public string Version { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
}
