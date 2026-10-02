using System.Collections.Concurrent;
using System.Diagnostics;
using FaceHubManagement.Agent.Configuration;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Services;

public abstract class ManagedServiceManagerBase : IManagedServiceManager
{
    private readonly IReadOnlyDictionary<string, ManagedServiceDefinition> _definitions;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operationLocks = new(StringComparer.OrdinalIgnoreCase);

    protected ManagedServiceManagerBase(IEnumerable<ManagedServiceDefinition> definitions)
    {
        _definitions = definitions.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<ManagedServiceInfo>> GetAllAsync(CancellationToken cancellationToken)
    {
        Task<ManagedServiceInfo>[] tasks = _definitions.Values
            .Select(definition => QueryAsync(definition, cancellationToken))
            .ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public Task<ManagedServiceInfo> GetAsync(string serviceId, CancellationToken cancellationToken) =>
        QueryAsync(GetDefinition(serviceId), cancellationToken);

    public Task<ManagedServiceInfo> StartAsync(string serviceId, CancellationToken cancellationToken) =>
        ExecuteExclusiveAsync(serviceId, ServiceOperation.Start, cancellationToken);

    public Task<ManagedServiceInfo> StopAsync(string serviceId, CancellationToken cancellationToken) =>
        ExecuteExclusiveAsync(serviceId, ServiceOperation.Stop, cancellationToken);

    public Task<ManagedServiceInfo> RestartAsync(string serviceId, CancellationToken cancellationToken) =>
        ExecuteExclusiveAsync(serviceId, ServiceOperation.Restart, cancellationToken);

    protected abstract Task<ManagedServiceInfo> QueryAsync(
        ManagedServiceDefinition definition,
        CancellationToken cancellationToken);

    protected abstract Task ExecuteOperationAsync(
        ManagedServiceDefinition definition,
        ServiceOperation operation,
        CancellationToken cancellationToken);

    protected static long? GetProcessUptimeSeconds(int? processId)
    {
        if (processId is null or <= 0)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById(processId.Value);
            DateTimeOffset started = process.StartTime.ToUniversalTime();
            return Math.Max(0, (long)(DateTimeOffset.UtcNow - started).TotalSeconds);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ManagedServiceInfo> ExecuteExclusiveAsync(
        string serviceId,
        ServiceOperation operation,
        CancellationToken cancellationToken)
    {
        ManagedServiceDefinition definition = GetDefinition(serviceId);
        SemaphoreSlim gate = _operationLocks.GetOrAdd(definition.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteOperationAsync(definition, operation, cancellationToken).ConfigureAwait(false);
            return await QueryAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    protected ManagedServiceDefinition GetDefinition(string serviceId)
    {
        if (!_definitions.TryGetValue(serviceId, out ManagedServiceDefinition? definition))
        {
            throw new KeyNotFoundException($"Service id '{serviceId}' is not managed by this agent.");
        }
        return definition;
    }

    protected enum ServiceOperation
    {
        Start,
        Stop,
        Restart
    }
}
