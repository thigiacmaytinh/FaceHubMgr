namespace FaceHubManagement.Core.Models;

public sealed class SystemInfoSnapshot
{
    public string MachineName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string OsArchitecture { get; set; } = string.Empty;
    public string ProcessArchitecture { get; set; } = string.Empty;
    public string FrameworkDescription { get; set; } = string.Empty;
    public string CpuName { get; set; } = string.Empty;
    public int LogicalProcessorCount { get; set; }
    public long SystemUptimeSeconds { get; set; }
    public string? GpuName { get; set; }
    public string? NvidiaDriverVersion { get; set; }
    public string? CudaDriverVersion { get; set; }
    public List<NetworkInterfaceInfo> NetworkInterfaces { get; set; } = [];
}
