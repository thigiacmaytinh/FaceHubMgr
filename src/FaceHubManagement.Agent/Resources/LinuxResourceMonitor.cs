using System.Globalization;
using System.Runtime.InteropServices;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Resources;

public sealed class LinuxResourceMonitor : IResourceMonitor
{
    private readonly NvmlMonitor _nvml;
    private readonly SemaphoreSlim _cpuGate = new(1, 1);
    private CpuSample? _previousCpu;

    public LinuxResourceMonitor(NvmlMonitor nvml)
    {
        _nvml = nvml;
    }

    public async Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        double cpu = await SampleCpuAsync(cancellationToken).ConfigureAwait(false);
        (ulong used, ulong total) = await ReadMemoryAsync(cancellationToken).ConfigureAwait(false);
        (ulong diskUsed, ulong diskTotal) = DiskUsageCollector.GetSystemDiskUsage();
        NvmlSnapshot gpu = _nvml.GetSnapshot();

        return new ResourceSnapshot
        {
            CpuUsagePercent = cpu,
            LogicalProcessorCount = Environment.ProcessorCount,
            MemoryUsedBytes = used,
            MemoryTotalBytes = total,
            DiskUsedBytes = diskUsed,
            DiskTotalBytes = diskTotal,
            GpuAvailable = gpu.Available,
            GpuName = gpu.Name,
            GpuUsagePercent = gpu.UtilizationPercent,
            GpuUsageSource = gpu.Available ? "NVIDIA NVML" : null,
            GpuStatusMessage = gpu.StatusMessage,

            // Linux has no WDDM CUDA engine counter. Instead, NVML identifies
            // processes that own a compute context and reports their SM (3D/Compute)
            // utilization. Using only those PIDs excludes NVDEC/NVENC-only and
            // unrelated graphics activity instead of relabeling device GPU-Util as CUDA.
            CudaUsageAvailable = gpu.ComputeUtilizationAvailable,
            CudaUsagePercent = gpu.ComputeUtilizationAvailable ? gpu.ComputeUtilizationPercent : 0,
            CudaUsageSource = gpu.ComputeUtilizationSource,
            CudaUsageStatusMessage = gpu.ComputeUtilizationStatusMessage
                ?? gpu.StatusMessage
                ?? "NVIDIA compute-process SM telemetry is unavailable.",

            VramAvailable = gpu.Available && gpu.MemoryTotalBytes > 0,
            VramUsedBytes = gpu.MemoryUsedBytes,
            VramTotalBytes = gpu.MemoryTotalBytes,
            NvidiaDriverVersion = gpu.DriverVersion,
            CudaDriverVersion = gpu.CudaDriverVersion
        };
    }

    public async Task<SystemInfoSnapshot> GetSystemInfoAsync(CancellationToken cancellationToken)
    {
        NvmlSnapshot gpu = _nvml.GetSnapshot();
        string cpuName = await ReadCpuNameAsync(cancellationToken).ConfigureAwait(false);
        long uptime = await ReadSystemUptimeSecondsAsync(cancellationToken).ConfigureAwait(false);

        return new SystemInfoSnapshot
        {
            MachineName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            CpuName = cpuName,
            LogicalProcessorCount = Environment.ProcessorCount,
            SystemUptimeSeconds = uptime,
            GpuName = gpu.Name,
            NvidiaDriverVersion = gpu.DriverVersion,
            CudaDriverVersion = gpu.CudaDriverVersion,
            NetworkInterfaces = NetworkInfoCollector.GetActiveIpv4Interfaces()
        };
    }

    private async Task<double> SampleCpuAsync(CancellationToken cancellationToken)
    {
        await _cpuGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CpuSample current = await ReadCpuSampleAsync(cancellationToken).ConfigureAwait(false);
            if (_previousCpu is null)
            {
                _previousCpu = current;
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                current = await ReadCpuSampleAsync(cancellationToken).ConfigureAwait(false);
            }

            CpuSample previous = _previousCpu.Value;
            _previousCpu = current;
            ulong idleDelta = current.Idle - previous.Idle;
            ulong totalDelta = current.Total - previous.Total;
            if (totalDelta == 0)
            {
                return 0;
            }

            double busy = 100.0 * (totalDelta - Math.Min(idleDelta, totalDelta)) / totalDelta;
            return Math.Clamp(busy, 0, 100);
        }
        finally
        {
            _cpuGate.Release();
        }
    }

    private static async Task<CpuSample> ReadCpuSampleAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader("/proc/stat");
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
        {
            throw new InvalidDataException("/proc/stat does not contain an aggregate CPU row.");
        }

        ulong[] values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Take(8)
            .Select(text => ulong.Parse(text, CultureInfo.InvariantCulture))
            .ToArray();
        if (values.Length < 4)
        {
            throw new InvalidDataException("/proc/stat CPU row is incomplete.");
        }

        ulong idle = values[3] + (values.Length > 4 ? values[4] : 0);
        ulong total = values.Aggregate(0UL, (sum, value) => sum + value);
        return new CpuSample(idle, total);
    }

    private static async Task<(ulong Used, ulong Total)> ReadMemoryAsync(CancellationToken cancellationToken)
    {
        string[] lines = await File.ReadAllLinesAsync("/proc/meminfo", cancellationToken).ConfigureAwait(false);
        ulong totalKb = ReadMemInfoValue(lines, "MemTotal:");
        ulong availableKb = ReadMemInfoValue(lines, "MemAvailable:");
        ulong total = checked(totalKb * 1024);
        ulong available = checked(availableKb * 1024);
        return (total - Math.Min(total, available), total);
    }

    private static ulong ReadMemInfoValue(IEnumerable<string> lines, string key)
    {
        string? line = lines.FirstOrDefault(value => value.StartsWith(key, StringComparison.Ordinal));
        if (line is null)
        {
            throw new InvalidDataException($"/proc/meminfo does not contain {key}");
        }

        string value = line[key.Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return ulong.Parse(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadCpuNameAsync(CancellationToken cancellationToken)
    {
        string[] lines = await File.ReadAllLinesAsync("/proc/cpuinfo", cancellationToken).ConfigureAwait(false);
        string? model = lines.FirstOrDefault(line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return "Unknown CPU";
        }
        int colon = model.IndexOf(':');
        return colon >= 0 ? model[(colon + 1)..].Trim() : model.Trim();
    }

    private static async Task<long> ReadSystemUptimeSecondsAsync(CancellationToken cancellationToken)
    {
        string content = await File.ReadAllTextAsync("/proc/uptime", cancellationToken).ConfigureAwait(false);
        string first = content.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return (long)Math.Max(0, double.Parse(first, CultureInfo.InvariantCulture));
    }

    private readonly record struct CpuSample(ulong Idle, ulong Total);
}
