using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FaceHubManagement.Agent.Configuration;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Resources;

public sealed class PortMonitor
{
    private readonly IReadOnlyList<MonitoredPortDefinition> _ports;

    public PortMonitor(
        IReadOnlyList<MonitoredPortDefinition> configuredPorts,
        IReadOnlyList<ManagedServiceDefinition> managedServices)
    {
        var ports = new Dictionary<int, MonitoredPortDefinition>();

        foreach (MonitoredPortDefinition definition in configuredPorts)
        {
            ports[definition.Port] = new MonitoredPortDefinition
            {
                Port = definition.Port,
                Name = definition.Name
            };
        }

        foreach (ManagedServiceDefinition service in managedServices)
        {
            foreach (int port in service.Ports)
            {
                if (!ports.ContainsKey(port))
                {
                    ports[port] = new MonitoredPortDefinition
                    {
                        Port = port,
                        Name = service.DisplayName
                    };
                }
            }
        }

        _ports = ports.Values
            .OrderBy(item => item.Port)
            .ToArray();
    }

    public IReadOnlyList<PortStatusInfo> GetSnapshot()
    {
        IPEndPoint[] listeners;
        string? monitorError = null;
        try
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch (Exception ex)
        {
            listeners = Array.Empty<IPEndPoint>();
            monitorError = ex.Message;
        }

        var endpointsByPort = listeners
            .GroupBy(endpoint => endpoint.Port)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(FormatEndpoint)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray());

        return _ports
            .Select(definition =>
            {
                endpointsByPort.TryGetValue(definition.Port, out IReadOnlyList<string>? endpoints);
                endpoints ??= Array.Empty<string>();
                return new PortStatusInfo
                {
                    Port = definition.Port,
                    Name = definition.Name,
                    Protocol = "TCP",
                    IsAvailable = monitorError is null,
                    IsListening = monitorError is null && endpoints.Count > 0,
                    Endpoints = endpoints,
                    StatusMessage = monitorError is null ? null : $"Port monitoring unavailable: {monitorError}"
                };
            })
            .ToArray();
    }

    private static string FormatEndpoint(IPEndPoint endpoint)
    {
        string address = endpoint.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{endpoint.Address}]"
            : endpoint.Address.ToString();
        return $"{address}:{endpoint.Port}";
    }
}
