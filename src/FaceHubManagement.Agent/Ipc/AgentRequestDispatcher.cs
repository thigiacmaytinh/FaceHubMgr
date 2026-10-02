using FaceHubManagement.Agent.Logging;
using FaceHubManagement.Agent.Firewall;
using FaceHubManagement.Agent.Resources;
using FaceHubManagement.Agent.Services;
using FaceHubManagement.Core.Ipc;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Ipc;

public sealed class AgentRequestDispatcher
{
    private readonly IManagedServiceManager _serviceManager;
    private readonly ServiceDiagnosticsCollector _serviceDiagnostics;
    private readonly IResourceMonitor _resourceMonitor;
    private readonly PortMonitor _portMonitor;
    private readonly IFirewallManager _firewallManager;
    private readonly RollingFileLog _log;
    private readonly DateTimeOffset _startedAt;

    public AgentRequestDispatcher(
        IManagedServiceManager serviceManager,
        ServiceDiagnosticsCollector serviceDiagnostics,
        IResourceMonitor resourceMonitor,
        PortMonitor portMonitor,
        IFirewallManager firewallManager,
        RollingFileLog log,
        DateTimeOffset startedAt)
    {
        _serviceManager = serviceManager;
        _serviceDiagnostics = serviceDiagnostics;
        _resourceMonitor = resourceMonitor;
        _portMonitor = portMonitor;
        _firewallManager = firewallManager;
        _log = log;
        _startedAt = startedAt;
    }

    public async Task HandleClientAsync(Stream stream, CancellationToken serverCancellationToken)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        requestCts.CancelAfter(TimeSpan.FromSeconds(45));

        IpcResponse response;
        IpcRequest? request = null;
        try
        {
            request = await FramedJsonProtocol.ReadAsync<IpcRequest>(stream, requestCts.Token).ConfigureAwait(false);
            response = await DispatchAsync(request, requestCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serverCancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Guid requestId = request?.RequestId ?? Guid.Empty;
            response = IpcResponse.Error(requestId, "request_failed", ex.Message);
            try
            {
                await _log.ErrorAsync("IPC", ex.ToString(), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        try
        {
            await FramedJsonProtocol.WriteAsync(stream, response, requestCts.Token).ConfigureAwait(false);
        }
        catch when (serverCancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case IpcCommands.Ping:
                return IpcResponse.Ok(request.RequestId, new AgentStatus
                {
                    Version = typeof(AgentRequestDispatcher).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                    Platform = OperatingSystem.IsWindows() ? "Windows" : "Linux",
                    StartedAt = _startedAt
                });

            case IpcCommands.GetResources:
                return IpcResponse.Ok(
                    request.RequestId,
                    await _resourceMonitor.GetSnapshotAsync(cancellationToken).ConfigureAwait(false));

            case IpcCommands.GetServices:
                return IpcResponse.Ok(
                    request.RequestId,
                    await _serviceManager.GetAllAsync(cancellationToken).ConfigureAwait(false));

            case IpcCommands.GetPorts:
                return IpcResponse.Ok(request.RequestId, _portMonitor.GetSnapshot());

            case IpcCommands.GetFirewallStatus:
                return IpcResponse.Ok(
                    request.RequestId,
                    await _firewallManager.GetStatusAsync(cancellationToken).ConfigureAwait(false));

            case IpcCommands.GetFirewallPorts:
                return IpcResponse.Ok(
                    request.RequestId,
                    await _firewallManager.GetPortsAsync(cancellationToken).ConfigureAwait(false));

            case IpcCommands.EnableFirewall:
                return await ExecuteFirewallStatusActionAsync(
                    request, "enable", _firewallManager.EnableAsync, cancellationToken).ConfigureAwait(false);

            case IpcCommands.DisableFirewall:
                return await ExecuteFirewallStatusActionAsync(
                    request, "disable", _firewallManager.DisableAsync, cancellationToken).ConfigureAwait(false);

            case IpcCommands.AddFirewallPort:
                return await ExecuteAddFirewallPortAsync(request, cancellationToken).ConfigureAwait(false);

            case IpcCommands.AllowFirewallPort:
                return await ExecuteFirewallPortActionAsync(
                    request, "allow", _firewallManager.AllowPortAsync, cancellationToken).ConfigureAwait(false);

            case IpcCommands.DenyFirewallPort:
                return await ExecuteFirewallPortActionAsync(
                    request, "deny", _firewallManager.DenyPortAsync, cancellationToken).ConfigureAwait(false);

            case IpcCommands.GetSystemInfo:
                return IpcResponse.Ok(
                    request.RequestId,
                    await _resourceMonitor.GetSystemInfoAsync(cancellationToken).ConfigureAwait(false));

            case IpcCommands.GetLogs:
                return IpcResponse.Ok(
                    request.RequestId,
                    await _log.GetRecentAsync(Math.Clamp(request.Limit ?? 100, 1, 1000), cancellationToken)
                        .ConfigureAwait(false));

            case IpcCommands.GetServiceDiagnostics:
                if (string.IsNullOrWhiteSpace(request.ServiceId))
                {
                    return IpcResponse.Error(request.RequestId, "invalid_service", "A managed service id is required.");
                }
                return IpcResponse.Ok(
                    request.RequestId,
                    await _serviceDiagnostics.GetAsync(request.ServiceId, request.Operation, cancellationToken)
                        .ConfigureAwait(false));

            case IpcCommands.StartService:
                return await ExecuteServiceActionAsync(request, "start", _serviceManager.StartAsync, cancellationToken)
                    .ConfigureAwait(false);

            case IpcCommands.StopService:
                return await ExecuteServiceActionAsync(request, "stop", _serviceManager.StopAsync, cancellationToken)
                    .ConfigureAwait(false);

            case IpcCommands.RestartService:
                return await ExecuteServiceActionAsync(request, "restart", _serviceManager.RestartAsync, cancellationToken)
                    .ConfigureAwait(false);

            default:
                return IpcResponse.Error(request.RequestId, "unsupported_command", $"Unknown command '{request.Command}'.");
        }
    }

    private async Task<IpcResponse> ExecuteFirewallStatusActionAsync(
        IpcRequest request,
        string verb,
        Func<CancellationToken, Task<FirewallStatusInfo>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await _log.InfoAsync("Firewall", $"Requested firewall {verb}.", cancellationToken).ConfigureAwait(false);
            FirewallStatusInfo status = await action(cancellationToken).ConfigureAwait(false);
            await _log.InfoAsync(
                "Firewall",
                $"Firewall {verb} completed. Available={status.IsAvailable}, Enabled={status.IsEnabled}, Partial={status.IsPartiallyEnabled}.",
                cancellationToken).ConfigureAwait(false);
            return IpcResponse.Ok(request.RequestId, status);
        }
        catch (Exception ex)
        {
            await _log.ErrorAsync("Firewall", $"Firewall {verb} failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return IpcResponse.Error(request.RequestId, "firewall_action_failed", ex.Message);
        }
    }

    private async Task<IpcResponse> ExecuteAddFirewallPortAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Port is not int port || port is < 1 or > 65535)
        {
            return IpcResponse.Error(request.RequestId, "invalid_port", "Port must be between 1 and 65535.");
        }

        try
        {
            FirewallPortInfo result = await _firewallManager.AddPortAsync(port, request.Name, cancellationToken)
                .ConfigureAwait(false);
            await _log.InfoAsync("Firewall", $"Added {port}/TCP to the firewall management list.", cancellationToken)
                .ConfigureAwait(false);
            return IpcResponse.Ok(request.RequestId, result);
        }
        catch (Exception ex)
        {
            await _log.ErrorAsync("Firewall", $"Unable to add {port}/TCP: {ex.Message}", cancellationToken)
                .ConfigureAwait(false);
            return IpcResponse.Error(request.RequestId, "firewall_port_add_failed", ex.Message);
        }
    }

    private async Task<IpcResponse> ExecuteFirewallPortActionAsync(
        IpcRequest request,
        string verb,
        Func<int, CancellationToken, Task<FirewallPortInfo>> action,
        CancellationToken cancellationToken)
    {
        if (request.Port is not int port || port is < 1 or > 65535)
        {
            return IpcResponse.Error(request.RequestId, "invalid_port", "Port must be between 1 and 65535.");
        }

        try
        {
            await _log.InfoAsync("Firewall", $"Requested {verb} for {port}/TCP.", cancellationToken).ConfigureAwait(false);
            FirewallPortInfo result = await action(port, cancellationToken).ConfigureAwait(false);
            await _log.InfoAsync("Firewall", $"{verb} for {port}/TCP completed with state {result.Access}.", cancellationToken)
                .ConfigureAwait(false);
            return IpcResponse.Ok(request.RequestId, result);
        }
        catch (KeyNotFoundException ex)
        {
            await _log.WarningAsync("Firewall", ex.Message, cancellationToken).ConfigureAwait(false);
            return IpcResponse.Error(request.RequestId, "firewall_port_not_managed", ex.Message);
        }
        catch (Exception ex)
        {
            await _log.ErrorAsync("Firewall", $"{verb} failed for {port}/TCP: {ex.Message}", cancellationToken)
                .ConfigureAwait(false);
            return IpcResponse.Error(request.RequestId, "firewall_port_action_failed", ex.Message);
        }
    }

    private async Task<IpcResponse> ExecuteServiceActionAsync(
        IpcRequest request,
        string verb,
        Func<string, CancellationToken, Task<ManagedServiceInfo>> action,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ServiceId))
        {
            return IpcResponse.Error(request.RequestId, "invalid_service", "A managed service id is required.");
        }

        try
        {
            await _log.ServiceEventAsync(
                    "INFO", request.ServiceId, verb, $"Requested {verb} for {request.ServiceId}.", cancellationToken)
                .ConfigureAwait(false);
            ManagedServiceInfo service = await action(request.ServiceId, cancellationToken).ConfigureAwait(false);
            await _log.ServiceEventAsync(
                    "INFO",
                    request.ServiceId,
                    verb,
                    $"{service.Name} {verb} completed with state {service.State}.",
                    cancellationToken)
                .ConfigureAwait(false);
            return IpcResponse.Ok(request.RequestId, service);
        }
        catch (KeyNotFoundException ex)
        {
            await _log.ServiceEventAsync(
                    "WARN", request.ServiceId, verb, ex.Message, cancellationToken)
                .ConfigureAwait(false);
            return IpcResponse.Error(request.RequestId, "service_not_managed", ex.Message);
        }
        catch (Exception ex)
        {
            await _log.ServiceEventAsync(
                    "ERROR",
                    request.ServiceId,
                    verb,
                    $"{verb} failed for {request.ServiceId}: {ex.Message}",
                    cancellationToken)
                .ConfigureAwait(false);
            return IpcResponse.Error(request.RequestId, "service_action_failed", ex.Message);
        }
    }
}
