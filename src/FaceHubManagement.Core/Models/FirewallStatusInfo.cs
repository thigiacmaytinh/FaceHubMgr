namespace FaceHubManagement.Core.Models;

public sealed class FirewallStatusInfo
{
    public string ProviderName { get; set; } = "Firewall";
    public bool IsAvailable { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsPartiallyEnabled { get; set; }
    public string? StatusMessage { get; set; }
    public IReadOnlyList<FirewallProfileInfo> Profiles { get; set; } = Array.Empty<FirewallProfileInfo>();
}

public sealed class FirewallProfileInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}
