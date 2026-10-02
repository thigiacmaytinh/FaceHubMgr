using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Resources;

public interface IResourceMonitor
{
    Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<SystemInfoSnapshot> GetSystemInfoAsync(CancellationToken cancellationToken);
}
