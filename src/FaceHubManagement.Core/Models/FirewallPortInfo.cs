namespace FaceHubManagement.Core.Models;

public enum FirewallPortAccess
{
    Unknown,
    NotAllowed,
    AllowedManaged,
    AllowedExternal,
    Blocked
}

public sealed class FirewallPortInfo
{
    public int Port { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = "TCP";
    public FirewallPortAccess Access { get; set; }
    public bool HasManagedAllowRule { get; set; }
    public string? StatusMessage { get; set; }
}
