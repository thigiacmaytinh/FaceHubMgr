namespace FaceHubManagement.Core.Models;

public sealed class PortStatusInfo
{
    public int Port { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = "TCP";
    public bool IsAvailable { get; set; } = true;
    public bool IsListening { get; set; }
    public IReadOnlyList<string> Endpoints { get; set; } = Array.Empty<string>();
    public string? StatusMessage { get; set; }
}
