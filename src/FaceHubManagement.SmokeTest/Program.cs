using FaceHubManagement.Core.Ipc;
using FaceHubManagement.Core.Models;
using FaceHubManagement.Core.Utilities;

var options = new AgentClientOptions();
bool exerciseActions = args.Contains("--actions", StringComparer.OrdinalIgnoreCase);
string? actionService = ReadOption(args, "--service");
string? pipe = ReadOption(args, "--pipe");
string? socket = ReadOption(args, "--socket");

if (!string.IsNullOrWhiteSpace(pipe))
{
    options.WindowsPipeName = pipe;
}
if (!string.IsNullOrWhiteSpace(socket))
{
    options.LinuxSocketPath = socket;
}

var client = new AgentClient(options);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

try
{
    AgentStatus agent = await client.PingAsync(cts.Token);
    Console.WriteLine($"Agent: {agent.Version} | {agent.Platform} | started {agent.StartedAt:O}");

    ResourceSnapshot resources = await client.GetResourcesAsync(cts.Token);
    Console.WriteLine($"CPU: {resources.CpuUsagePercent:0.0}%");
    Console.WriteLine($"RAM: {ByteFormatter.Format(resources.MemoryUsedBytes)} / {ByteFormatter.Format(resources.MemoryTotalBytes)}");
    Console.WriteLine(resources.GpuAvailable
        ? $"GPU: {resources.GpuName} {resources.GpuUsagePercent:0.0}% [{resources.GpuUsageSource ?? "unknown source"}]"
        : $"GPU: unavailable ({resources.GpuStatusMessage})");
    Console.WriteLine(resources.CudaUsageAvailable
        ? $"CUDA: {resources.CudaUsagePercent:0.0}% [{resources.CudaUsageSource ?? "unknown source"}]"
        : $"CUDA: unavailable ({resources.CudaUsageStatusMessage})");
    Console.WriteLine(resources.VramAvailable
        ? $"VRAM: {ByteFormatter.Format(resources.VramUsedBytes)} / {ByteFormatter.Format(resources.VramTotalBytes)}"
        : "VRAM: unavailable");

    IReadOnlyList<ManagedServiceInfo> services = await client.GetServicesAsync(cts.Token);
    foreach (ManagedServiceInfo service in services)
    {
        Console.WriteLine($"Service: {service.Id} | {service.State} | PID {service.ProcessId?.ToString() ?? "-"} | ports {string.Join(',', service.Ports)}");
    }

    FirewallStatusInfo firewall = await client.GetFirewallStatusAsync(cts.Token);
    Console.WriteLine($"Firewall: {firewall.ProviderName} | available={firewall.IsAvailable} | enabled={firewall.IsEnabled} | partial={firewall.IsPartiallyEnabled}");

    IReadOnlyList<FirewallPortInfo> firewallPorts = await client.GetFirewallPortsAsync(cts.Token);
    foreach (FirewallPortInfo port in firewallPorts)
    {
        Console.WriteLine($"Firewall port: {port.Port}/TCP | {port.Name} | {port.Access}");
    }

    SystemInfoSnapshot system = await client.GetSystemInfoAsync(cts.Token);
    Console.WriteLine($"System: {system.MachineName} | {system.OperatingSystem} | {system.CpuName}");

    IReadOnlyList<ServiceLogEntry> logs = await client.GetLogsAsync(10, cts.Token);
    Console.WriteLine($"Logs returned: {logs.Count}");

    if (exerciseActions)
    {
        if (string.IsNullOrWhiteSpace(actionService))
        {
            throw new ArgumentException("--actions requires --service <nginx|facehub|mongodb>.");
        }

        ManagedServiceInfo service = services.FirstOrDefault(item => item.Id.Equals(actionService, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Service '{actionService}' is not managed by the agent.");

        Console.WriteLine($"Action test target: {service.Id}, current state {service.State}");
        if (service.State == ServiceState.Running)
        {
            ManagedServiceInfo restarted = await client.RestartServiceAsync(service.Id, cts.Token);
            Console.WriteLine($"Restart result: {restarted.State}");
        }
        else
        {
            ManagedServiceInfo started = await client.StartServiceAsync(service.Id, cts.Token);
            Console.WriteLine($"Start result: {started.State}");
        }
    }

    Console.WriteLine("SMOKE TEST PASSED");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Console.Error.WriteLine("SMOKE TEST FAILED");
    return 1;
}

static string? ReadOption(string[] arguments, string name)
{
    for (int index = 0; index < arguments.Length - 1; index++)
    {
        if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }
    return null;
}
