namespace FaceHubManagement.Core.Models;

public sealed class ResourceSnapshot
{
    public double CpuUsagePercent { get; set; }
    public int LogicalProcessorCount { get; set; }
    public ulong MemoryUsedBytes { get; set; }
    public ulong MemoryTotalBytes { get; set; }
    public ulong DiskUsedBytes { get; set; }
    public ulong DiskTotalBytes { get; set; }

    public bool GpuAvailable { get; set; }
    public string? GpuName { get; set; }
    public double GpuUsagePercent { get; set; }
    public string? GpuUsageSource { get; set; }
    public string? GpuStatusMessage { get; set; }

    public bool CudaUsageAvailable { get; set; }
    public double CudaUsagePercent { get; set; }
    public string? CudaUsageSource { get; set; }
    public string? CudaUsageStatusMessage { get; set; }

    public bool VramAvailable { get; set; }
    public ulong VramUsedBytes { get; set; }
    public ulong VramTotalBytes { get; set; }

    public string? NvidiaDriverVersion { get; set; }
    public string? CudaDriverVersion { get; set; }
}
