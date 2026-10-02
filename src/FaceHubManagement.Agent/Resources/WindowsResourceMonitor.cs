using System.Runtime.InteropServices;
using Microsoft.Win32;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Resources;

public sealed class WindowsResourceMonitor : IResourceMonitor
{
    private readonly NvmlMonitor _nvml;
    private readonly WindowsGpuEngineMonitor _gpuEngines;
    private readonly SemaphoreSlim _cpuGate = new(1, 1);
    private CpuSample? _previousCpu;

    public WindowsResourceMonitor(NvmlMonitor nvml, WindowsGpuEngineMonitor gpuEngines)
    {
        _nvml = nvml;
        _gpuEngines = gpuEngines;
    }

    public async Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        double cpu = await SampleCpuAsync(cancellationToken).ConfigureAwait(false);
        (ulong used, ulong total) = ReadMemory();
        (ulong diskUsed, ulong diskTotal) = DiskUsageCollector.GetSystemDiskUsage();
        NvmlSnapshot nvml = _nvml.GetSnapshot();
        WindowsGpuEngineSnapshot wddm = await _gpuEngines
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        bool gpuAvailable = wddm.Available || nvml.Available;
        double gpuUsage = wddm.Available
            ? wddm.GpuUsagePercent
            : nvml.UtilizationPercent;
        string? gpuUsageSource = wddm.Available
            ? wddm.GpuUsageSource
            : nvml.Available ? "NVIDIA NVML (fallback)" : null;

        return new ResourceSnapshot
        {
            CpuUsagePercent = cpu,
            LogicalProcessorCount = Environment.ProcessorCount,
            MemoryUsedBytes = used,
            MemoryTotalBytes = total,
            DiskUsedBytes = diskUsed,
            DiskTotalBytes = diskTotal,

            GpuAvailable = gpuAvailable,
            GpuName = nvml.Name ?? (wddm.Available ? "WDDM GPU" : null),
            GpuUsagePercent = gpuUsage,
            GpuUsageSource = gpuUsageSource,
            GpuStatusMessage = gpuAvailable
                ? null
                : CombineStatus(wddm.StatusMessage, nvml.StatusMessage),

            CudaUsageAvailable = wddm.CudaAvailable,
            CudaUsagePercent = wddm.CudaUsagePercent,
            CudaUsageSource = wddm.CudaUsageSource,
            CudaUsageStatusMessage = wddm.CudaStatusMessage,

            VramAvailable = nvml.Available && nvml.MemoryTotalBytes > 0,
            VramUsedBytes = nvml.MemoryUsedBytes,
            VramTotalBytes = nvml.MemoryTotalBytes,
            NvidiaDriverVersion = nvml.DriverVersion,
            CudaDriverVersion = nvml.CudaDriverVersion
        };
    }

    public Task<SystemInfoSnapshot> GetSystemInfoAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WindowsResourceMonitor is only supported on Windows.");
        }

        NvmlSnapshot gpu = _nvml.GetSnapshot();
        string cpuName;
        try
        {
            cpuName = Convert.ToString(Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString",
                null))?.Trim() ?? "Unknown CPU";
        }
        catch
        {
            cpuName = "Unknown CPU";
        }

        var info = new SystemInfoSnapshot
        {
            MachineName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            CpuName = cpuName,
            LogicalProcessorCount = Environment.ProcessorCount,
            SystemUptimeSeconds = Math.Max(0, Environment.TickCount64 / 1000),
            GpuName = gpu.Name,
            NvidiaDriverVersion = gpu.DriverVersion,
            CudaDriverVersion = gpu.CudaDriverVersion,
            NetworkInterfaces = NetworkInfoCollector.GetActiveIpv4Interfaces()
        };
        return Task.FromResult(info);
    }

    private static string CombineStatus(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second ?? "GPU monitoring is unavailable.";
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return $"{first} {second}";
    }

    private async Task<double> SampleCpuAsync(CancellationToken cancellationToken)
    {
        await _cpuGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CpuSample current = ReadCpuSample();
            if (_previousCpu is null)
            {
                _previousCpu = current;
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                current = ReadCpuSample();
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

    private static CpuSample ReadCpuSample()
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetSystemTimes failed.");
        }

        ulong idleValue = ToUInt64(idle);
        ulong kernelValue = ToUInt64(kernel);
        ulong userValue = ToUInt64(user);
        return new CpuSample(idleValue, kernelValue + userValue);
    }

    private static (ulong Used, ulong Total) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GlobalMemoryStatusEx failed.");
        }

        return (status.TotalPhys - status.AvailPhys, status.TotalPhys);
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    private readonly record struct CpuSample(ulong Idle, ulong Total);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
