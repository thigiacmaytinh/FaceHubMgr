using FaceHubManagement.Agent.Configuration;
using FaceHubManagement.Agent.Platform;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Services;

public sealed class LinuxSystemdServiceManager : ManagedServiceManagerBase
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private readonly string _systemctlPath;

    public LinuxSystemdServiceManager(IEnumerable<ManagedServiceDefinition> definitions) : base(definitions)
    {
        _systemctlPath = File.Exists("/usr/bin/systemctl") ? "/usr/bin/systemctl" : "/bin/systemctl";
    }

    protected override async Task<ManagedServiceInfo> QueryAsync(
        ManagedServiceDefinition definition,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            _systemctlPath,
            ["show", "--no-pager", "--property=LoadState", "--property=ActiveState", "--property=SubState", "--property=MainPID", definition.LinuxUnitName],
            CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return CreateInfo(definition, ServiceState.Failed, null, null, NormalizeError(result));
        }

        Dictionary<string, string> values = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        values.TryGetValue("LoadState", out string? load);
        values.TryGetValue("ActiveState", out string? active);
        values.TryGetValue("SubState", out string? sub);
        if (string.Equals(load, "not-found", StringComparison.OrdinalIgnoreCase))
        {
            return CreateInfo(definition, ServiceState.Failed, null, null, $"systemd unit '{definition.LinuxUnitName}' was not found.");
        }
        int? pid = values.TryGetValue("MainPID", out string? pidText) && int.TryParse(pidText, out int parsedPid) && parsedPid > 0
            ? parsedPid
            : null;

        ServiceState state = MapState(active, sub);
        string? detail = state == ServiceState.Failed ? $"ActiveState={active}, SubState={sub}" : null;
        return CreateInfo(definition, state, pid, GetProcessUptimeSeconds(pid), detail);
    }

    protected override async Task ExecuteOperationAsync(
        ManagedServiceDefinition definition,
        ServiceOperation operation,
        CancellationToken cancellationToken)
    {
        string verb = operation switch
        {
            ServiceOperation.Start => "start",
            ServiceOperation.Stop => "stop",
            ServiceOperation.Restart => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        ProcessResult result = await ProcessRunner.RunAsync(
            _systemctlPath,
            [verb, definition.LinuxUnitName],
            CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to {verb} {definition.DisplayName}: {NormalizeError(result)}");
        }

        ServiceState target = operation == ServiceOperation.Stop ? ServiceState.Stopped : ServiceState.Running;
        await WaitForStateAsync(definition, target, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForStateAsync(
        ManagedServiceDefinition definition,
        ServiceState target,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            ManagedServiceInfo current = await QueryAsync(definition, timeoutCts.Token).ConfigureAwait(false);
            if (current.State == target)
            {
                return;
            }
            if (current.State == ServiceState.Failed && target == ServiceState.Running)
            {
                throw new InvalidOperationException($"{definition.DisplayName} entered systemd failed state.");
            }
            await Task.Delay(500, timeoutCts.Token).ConfigureAwait(false);
        }
    }

    private static ServiceState MapState(string? active, string? sub)
    {
        return active?.ToLowerInvariant() switch
        {
            "active" when string.Equals(sub, "running", StringComparison.OrdinalIgnoreCase) => ServiceState.Running,
            "activating" => ServiceState.Starting,
            "deactivating" => ServiceState.Stopping,
            "inactive" => ServiceState.Stopped,
            "failed" => ServiceState.Failed,
            "active" => ServiceState.Running,
            _ => ServiceState.Unknown
        };
    }

    private static ManagedServiceInfo CreateInfo(
        ManagedServiceDefinition definition,
        ServiceState state,
        int? pid,
        long? uptime,
        string? detail)
    {
        return new ManagedServiceInfo
        {
            Id = definition.Id,
            Name = definition.DisplayName,
            Description = definition.Description,
            State = state,
            ProcessId = pid,
            UptimeSeconds = uptime,
            Ports = definition.Ports,
            Detail = detail
        };
    }

    private static string NormalizeError(ProcessResult result)
    {
        string text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(text) ? $"systemctl exited with code {result.ExitCode}." : text.Trim();
    }
}
