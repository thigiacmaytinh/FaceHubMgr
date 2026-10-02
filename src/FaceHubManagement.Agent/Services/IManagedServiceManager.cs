using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Services;

public interface IManagedServiceManager
{
    Task<IReadOnlyList<ManagedServiceInfo>> GetAllAsync(CancellationToken cancellationToken);
    Task<ManagedServiceInfo> GetAsync(string serviceId, CancellationToken cancellationToken);
    Task<ManagedServiceInfo> StartAsync(string serviceId, CancellationToken cancellationToken);
    Task<ManagedServiceInfo> StopAsync(string serviceId, CancellationToken cancellationToken);
    Task<ManagedServiceInfo> RestartAsync(string serviceId, CancellationToken cancellationToken);
}
