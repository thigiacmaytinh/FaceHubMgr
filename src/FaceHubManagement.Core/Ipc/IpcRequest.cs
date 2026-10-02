namespace FaceHubManagement.Core.Ipc;

public sealed class IpcRequest
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public string Command { get; set; } = string.Empty;
    public string? ServiceId { get; set; }
    public string? Operation { get; set; }
    public int? Limit { get; set; }
    public int? Port { get; set; }
    public string? Name { get; set; }
}
