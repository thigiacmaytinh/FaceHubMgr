using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Firewall;

public interface IFirewallManager
{
    Task<FirewallStatusInfo> GetStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<FirewallPortInfo>> GetPortsAsync(CancellationToken cancellationToken);
    Task<FirewallStatusInfo> EnableAsync(CancellationToken cancellationToken);
    Task<FirewallStatusInfo> DisableAsync(CancellationToken cancellationToken);
    Task<FirewallPortInfo> AddPortAsync(int port, string? name, CancellationToken cancellationToken);
    Task<FirewallPortInfo> AllowPortAsync(int port, CancellationToken cancellationToken);
    Task<FirewallPortInfo> DenyPortAsync(int port, CancellationToken cancellationToken);
}
