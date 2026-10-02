using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Core.Ipc;

public sealed class AgentClient
{
    private readonly AgentClientOptions _options;

    public AgentClient(AgentClientOptions options)
    {
        _options = options;
    }

    public Task<AgentStatus> PingAsync(CancellationToken cancellationToken = default) =>
        SendAsync<AgentStatus>(
            new IpcRequest { Command = IpcCommands.Ping }, cancellationToken, _options.HealthRequestTimeout);

    public Task<ResourceSnapshot> GetResourcesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ResourceSnapshot>(
            new IpcRequest { Command = IpcCommands.GetResources }, cancellationToken, _options.TelemetryRequestTimeout);

    public Task<IReadOnlyList<ManagedServiceInfo>> GetServicesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<ManagedServiceInfo>>(
            new IpcRequest { Command = IpcCommands.GetServices }, cancellationToken, _options.TelemetryRequestTimeout);

    public Task<IReadOnlyList<PortStatusInfo>> GetPortsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<PortStatusInfo>>(
            new IpcRequest { Command = IpcCommands.GetPorts }, cancellationToken, _options.TelemetryRequestTimeout);

    public Task<FirewallStatusInfo> GetFirewallStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync<FirewallStatusInfo>(
            new IpcRequest { Command = IpcCommands.GetFirewallStatus },
            cancellationToken,
            _options.FirewallRequestTimeout);

    public Task<IReadOnlyList<FirewallPortInfo>> GetFirewallPortsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<FirewallPortInfo>>(
            new IpcRequest { Command = IpcCommands.GetFirewallPorts },
            cancellationToken,
            _options.FirewallRequestTimeout);

    public Task<FirewallStatusInfo> EnableFirewallAsync(CancellationToken cancellationToken = default) =>
        SendAsync<FirewallStatusInfo>(
            new IpcRequest { Command = IpcCommands.EnableFirewall },
            cancellationToken,
            _options.FirewallRequestTimeout);

    public Task<FirewallStatusInfo> DisableFirewallAsync(CancellationToken cancellationToken = default) =>
        SendAsync<FirewallStatusInfo>(
            new IpcRequest { Command = IpcCommands.DisableFirewall },
            cancellationToken,
            _options.FirewallRequestTimeout);

    public Task<FirewallPortInfo> AddFirewallPortAsync(int port, string? name, CancellationToken cancellationToken = default) =>
        SendAsync<FirewallPortInfo>(
            new IpcRequest { Command = IpcCommands.AddFirewallPort, Port = port, Name = name },
            cancellationToken,
            _options.FirewallRequestTimeout);

    public Task<FirewallPortInfo> AllowFirewallPortAsync(int port, CancellationToken cancellationToken = default) =>
        SendAsync<FirewallPortInfo>(
            new IpcRequest { Command = IpcCommands.AllowFirewallPort, Port = port },
            cancellationToken,
            _options.FirewallRequestTimeout);

    public Task<FirewallPortInfo> DenyFirewallPortAsync(int port, CancellationToken cancellationToken = default) =>
        SendAsync<FirewallPortInfo>(
            new IpcRequest { Command = IpcCommands.DenyFirewallPort, Port = port },
            cancellationToken,
            _options.FirewallRequestTimeout);

    public Task<SystemInfoSnapshot> GetSystemInfoAsync(CancellationToken cancellationToken = default) =>
        SendAsync<SystemInfoSnapshot>(
            new IpcRequest { Command = IpcCommands.GetSystemInfo }, cancellationToken, _options.TelemetryRequestTimeout);

    public Task<IReadOnlyList<ServiceLogEntry>> GetLogsAsync(int limit, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<ServiceLogEntry>>(
            new IpcRequest { Command = IpcCommands.GetLogs, Limit = Math.Clamp(limit, 1, 1000) },
            cancellationToken,
            _options.TelemetryRequestTimeout);

    public Task<ServiceDiagnosticsInfo> GetServiceDiagnosticsAsync(
        string serviceId,
        string? operation = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<ServiceDiagnosticsInfo>(
            new IpcRequest
            {
                Command = IpcCommands.GetServiceDiagnostics,
                ServiceId = serviceId,
                Operation = operation
            },
            cancellationToken,
            _options.ServiceActionTimeout);

    public Task<ManagedServiceInfo> StartServiceAsync(string serviceId, CancellationToken cancellationToken = default) =>
        SendAsync<ManagedServiceInfo>(
            new IpcRequest { Command = IpcCommands.StartService, ServiceId = serviceId },
            cancellationToken,
            _options.ServiceActionTimeout);

    public Task<ManagedServiceInfo> StopServiceAsync(string serviceId, CancellationToken cancellationToken = default) =>
        SendAsync<ManagedServiceInfo>(
            new IpcRequest { Command = IpcCommands.StopService, ServiceId = serviceId },
            cancellationToken,
            _options.ServiceActionTimeout);

    public Task<ManagedServiceInfo> RestartServiceAsync(string serviceId, CancellationToken cancellationToken = default) =>
        SendAsync<ManagedServiceInfo>(
            new IpcRequest { Command = IpcCommands.RestartService, ServiceId = serviceId },
            cancellationToken,
            _options.ServiceActionTimeout);

    private async Task<T> SendAsync<T>(
        IpcRequest request,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(requestTimeout ?? _options.RequestTimeout);

        try
        {
            await using Stream stream = await ConnectAsync(requestCts.Token).ConfigureAwait(false);
            await FramedJsonProtocol.WriteAsync(stream, request, requestCts.Token).ConfigureAwait(false);
            IpcResponse response = await FramedJsonProtocol.ReadAsync<IpcResponse>(stream, requestCts.Token)
                .ConfigureAwait(false);

            if (response.RequestId != request.RequestId)
            {
                throw new AgentClientException("protocol_error", "Agent returned a mismatched request identifier.");
            }

            if (!response.Success)
            {
                throw new AgentClientException(
                    response.ErrorCode ?? "agent_error",
                    response.ErrorMessage ?? "The management agent rejected the request.");
            }

            if (response.Payload is null)
            {
                throw new AgentClientException("protocol_error", "Agent returned a successful response without a payload.");
            }

            T? payload = response.Payload.Value.Deserialize<T>(JsonDefaults.Options);
            return payload ?? throw new AgentClientException("protocol_error", "Agent response payload was invalid.");
        }
        catch (AgentClientException)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentClientException(
                "timeout",
                $"Agent request '{request.Command}' did not respond before the timeout.",
                ex);
        }
        catch (Exception ex)
        {
            throw new AgentClientException(
                "connection_failed",
                $"Unable to communicate with the management agent for '{request.Command}'.",
                ex);
        }
    }

    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_options.ConnectTimeout);

        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(
                ".",
                _options.WindowsPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(_options.LinuxSocketPath), connectCts.Token)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new PlatformNotSupportedException("FaceHub Management supports Windows and Linux desktop hosts.");
    }
}
