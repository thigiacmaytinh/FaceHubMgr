using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FaceHubManagement.Agent.Resources;

public sealed class WindowsGpuEngineSnapshot
{
    public bool Available { get; init; }
    public double GpuUsagePercent { get; init; }
    public bool CudaAvailable { get; init; }
    public double CudaUsagePercent { get; init; }
    public string? GpuUsageSource { get; init; }
    public string? CudaUsageSource { get; init; }
    public string? StatusMessage { get; init; }
    public string? CudaStatusMessage { get; init; }
}

/// <summary>
/// Reads the Windows WDDM GPU Engine performance counters used by Task Manager.
/// Overall GPU usage is the busiest physical engine, matching Task Manager's
/// aggregation rule. CUDA usage is the busiest CUDA engine; if the driver does
/// not expose an engine named CUDA, Compute_* engines are used as a documented
/// fallback because recent NVIDIA/WDDM combinations may expose compute that way.
/// </summary>
public sealed class WindowsGpuEngineMonitor : IDisposable
{
    private const uint ErrorSuccess = 0x00000000;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCStatusValidData = 0x00000000;
    private const uint PdhCStatusNewData = 0x00000001;
    private const uint PdhFmtDouble = 0x00000200;

    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IntPtr _query;
    private IntPtr _counter;
    private bool _baselineCollected;
    private bool _disposed;
    private readonly string? _initializationError;

    public WindowsGpuEngineMonitor()
    {
        if (!OperatingSystem.IsWindows())
        {
            _initializationError = "Windows GPU Engine counters are only available on Windows.";
            return;
        }

        uint status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != ErrorSuccess)
        {
            _initializationError = $"PdhOpenQueryW failed with 0x{status:X8}.";
            return;
        }

        status = PdhAddEnglishCounterW(_query, CounterPath, IntPtr.Zero, out _counter);
        if (status != ErrorSuccess)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _initializationError = $"GPU Engine counter is unavailable (0x{status:X8}). WDDM 2.0+ is required.";
        }
    }

    public async Task<WindowsGpuEngineSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_initializationError is not null || _query == IntPtr.Zero || _counter == IntPtr.Zero)
        {
            return Unavailable(_initializationError ?? "Windows GPU Engine counter is unavailable.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            uint collectStatus = PdhCollectQueryData(_query);
            if (collectStatus != ErrorSuccess)
            {
                return Unavailable($"PdhCollectQueryData failed with 0x{collectStatus:X8}.");
            }

            if (!_baselineCollected)
            {
                _baselineCollected = true;
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                collectStatus = PdhCollectQueryData(_query);
                if (collectStatus != ErrorSuccess)
                {
                    return Unavailable($"PdhCollectQueryData failed with 0x{collectStatus:X8}.");
                }
            }

            IReadOnlyList<EngineCounterSample> samples = ReadFormattedSamples();
            if (samples.Count == 0)
            {
                return new WindowsGpuEngineSnapshot
                {
                    Available = true,
                    GpuUsagePercent = 0,
                    CudaAvailable = false,
                    CudaUsagePercent = 0,
                    GpuUsageSource = "Task Manager / WDDM",
                    StatusMessage = null,
                    CudaStatusMessage = "No CUDA/Compute WDDM engine is currently exposed."
                };
            }

            Dictionary<string, double> physicalEngines = AggregateByPhysicalEngine(samples);
            double overall = physicalEngines.Count == 0
                ? 0
                : physicalEngines.Values.Max();

            bool hasExactCuda = samples.Any(sample => IsExactCudaEngine(sample.InstanceName));
            IEnumerable<EngineCounterSample> cudaSamples = hasExactCuda
                ? samples.Where(sample => IsExactCudaEngine(sample.InstanceName))
                : samples.Where(sample => IsComputeEngine(sample.InstanceName));

            List<EngineCounterSample> selectedCudaSamples = cudaSamples.ToList();
            Dictionary<string, double> cudaEngines = AggregateByPhysicalEngine(selectedCudaSamples);
            bool cudaAvailable = cudaEngines.Count > 0;
            double cuda = cudaAvailable ? cudaEngines.Values.Max() : 0;

            return new WindowsGpuEngineSnapshot
            {
                Available = true,
                GpuUsagePercent = Math.Clamp(overall, 0, 100),
                CudaAvailable = cudaAvailable,
                CudaUsagePercent = Math.Clamp(cuda, 0, 100),
                GpuUsageSource = "Task Manager / WDDM",
                CudaUsageSource = cudaAvailable
                    ? hasExactCuda
                        ? "CUDA engine · Task Manager / WDDM"
                        : "Compute engine · Task Manager / WDDM"
                    : null,
                StatusMessage = null,
                CudaStatusMessage = cudaAvailable
                    ? null
                    : "CUDA/Compute engine is not exposed by the current WDDM/NVIDIA driver configuration."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable($"Windows GPU Engine query failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                _counter = IntPtr.Zero;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private IReadOnlyList<EngineCounterSample> ReadFormattedSamples()
    {
        uint bufferSize = 0;
        uint itemCount = 0;
        uint status = PdhGetFormattedCounterArrayW(
            _counter,
            PdhFmtDouble,
            ref bufferSize,
            ref itemCount,
            IntPtr.Zero);

        if (status != PdhMoreData && status != ErrorSuccess)
        {
            throw new Win32Exception(unchecked((int)status),
                $"PdhGetFormattedCounterArrayW sizing failed with 0x{status:X8}.");
        }

        if (bufferSize == 0 || itemCount == 0)
        {
            return Array.Empty<EngineCounterSample>();
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
        try
        {
            status = PdhGetFormattedCounterArrayW(
                _counter,
                PdhFmtDouble,
                ref bufferSize,
                ref itemCount,
                buffer);

            if (status != ErrorSuccess)
            {
                throw new Win32Exception(unchecked((int)status),
                    $"PdhGetFormattedCounterArrayW failed with 0x{status:X8}.");
            }

            int itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            var result = new List<EngineCounterSample>(checked((int)itemCount));
            for (int index = 0; index < itemCount; index++)
            {
                IntPtr itemPtr = IntPtr.Add(buffer, checked(index * itemSize));
                PdhFmtCounterValueItem item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(itemPtr);
                if (item.Name == IntPtr.Zero)
                {
                    continue;
                }

                if (item.Value.CStatus != PdhCStatusValidData &&
                    item.Value.CStatus != PdhCStatusNewData)
                {
                    continue;
                }

                string? instanceName = Marshal.PtrToStringUni(item.Name);
                if (string.IsNullOrWhiteSpace(instanceName) || double.IsNaN(item.Value.DoubleValue))
                {
                    continue;
                }

                result.Add(new EngineCounterSample(
                    instanceName,
                    Math.Clamp(item.Value.DoubleValue, 0, 100)));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Dictionary<string, double> AggregateByPhysicalEngine(
        IEnumerable<EngineCounterSample> samples)
    {
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (EngineCounterSample sample in samples)
        {
            string key = GetPhysicalEngineKey(sample.InstanceName);
            engines.TryGetValue(key, out double current);
            engines[key] = Math.Clamp(current + sample.UtilizationPercent, 0, 100);
        }

        return engines;
    }

    private static string GetPhysicalEngineKey(string instanceName)
    {
        int luidIndex = instanceName.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
        int engineTypeIndex = instanceName.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
        if (luidIndex >= 0 && engineTypeIndex > luidIndex)
        {
            return instanceName[(luidIndex + 1)..engineTypeIndex];
        }

        int pidPrefixEnd = instanceName.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
        return pidPrefixEnd > 0 ? instanceName[pidPrefixEnd..] : instanceName;
    }

    private static bool IsExactCudaEngine(string instanceName) =>
        instanceName.Contains("_engtype_CUDA", StringComparison.OrdinalIgnoreCase);

    private static bool IsComputeEngine(string instanceName) =>
        instanceName.Contains("_engtype_Compute", StringComparison.OrdinalIgnoreCase);

    private static WindowsGpuEngineSnapshot Unavailable(string message) => new()
    {
        Available = false,
        GpuUsagePercent = 0,
        CudaAvailable = false,
        CudaUsagePercent = 0,
        GpuUsageSource = null,
        CudaUsageSource = null,
        StatusMessage = message,
        CudaStatusMessage = message
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly record struct EngineCounterSample(string InstanceName, double UtilizationPercent);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(
        string? dataSource,
        IntPtr userData,
        out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(
        IntPtr query,
        string fullCounterPath,
        IntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
