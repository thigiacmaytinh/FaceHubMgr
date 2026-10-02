using System.Runtime.InteropServices;

namespace FaceHubManagement.Agent.Resources;

public sealed class NvmlSnapshot
{
    public bool Available { get; init; }
    public string? Name { get; init; }
    public double UtilizationPercent { get; init; }
    public ulong MemoryUsedBytes { get; init; }
    public ulong MemoryTotalBytes { get; init; }
    public string? DriverVersion { get; init; }
    public string? CudaDriverVersion { get; init; }
    public string? StatusMessage { get; init; }

    // CUDA/compute-specific telemetry. On Linux this is derived from the SM
    // utilization samples of processes that currently own an NVML compute
    // context, rather than from device-wide GPU utilization.
    public bool ComputeUtilizationAvailable { get; init; }
    public double ComputeUtilizationPercent { get; init; }
    public string? ComputeUtilizationSource { get; init; }
    public string? ComputeUtilizationStatusMessage { get; init; }
    public int ComputeProcessCount { get; init; }
}

public sealed class NvmlMonitor : IDisposable
{
    private const int NvmlSuccess = 0;
    private const int NvmlErrorNotSupported = 3;
    private const int NvmlErrorNoPermission = 4;
    private const int NvmlErrorNotFound = 6;
    private const int NvmlErrorInsufficientSize = 7;
    private const ulong ProcessSampleFreshnessWindowUs = 2_500_000;

    private readonly object _gate = new();
    private IntPtr _library;
    private IntPtr _device;
    private bool _initialized;
    private string? _initializationError;
    private ulong _lastProcessUtilizationTimestamp;

    private NvmlInitDelegate? _init;
    private NvmlShutdownDelegate? _shutdown;
    private NvmlDeviceGetHandleByIndexDelegate? _getHandle;
    private NvmlDeviceGetUtilizationRatesDelegate? _getUtilization;
    private NvmlDeviceGetMemoryInfoDelegate? _getMemory;
    private NvmlDeviceGetNameDelegate? _getName;
    private NvmlSystemGetDriverVersionDelegate? _getDriverVersion;
    private NvmlSystemGetCudaDriverVersionDelegate? _getCudaDriverVersion;
    private NvmlDeviceGetRunningProcessesDelegate? _getComputeRunningProcesses;
    private NvmlDeviceGetProcessUtilizationDelegate? _getProcessUtilization;

    public NvmlMonitor()
    {
        Initialize();
    }

    public NvmlSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                return new NvmlSnapshot
                {
                    Available = false,
                    ComputeUtilizationAvailable = false,
                    StatusMessage = _initializationError ?? "NVIDIA NVML is unavailable.",
                    ComputeUtilizationStatusMessage = _initializationError ?? "NVIDIA NVML is unavailable."
                };
            }

            try
            {
                var utilization = new NvmlUtilization();
                var memory = new NvmlMemory();
                int utilizationResult = _getUtilization!(_device, ref utilization);
                int memoryResult = _getMemory!(_device, ref memory);
                if (utilizationResult != NvmlSuccess || memoryResult != NvmlSuccess)
                {
                    string error = $"NVML query failed (utilization={utilizationResult}, memory={memoryResult}).";
                    return new NvmlSnapshot
                    {
                        Available = false,
                        ComputeUtilizationAvailable = false,
                        StatusMessage = error,
                        ComputeUtilizationStatusMessage = error
                    };
                }

                ComputeUtilizationResult compute = OperatingSystem.IsLinux()
                    ? ReadComputeUtilization()
                    : ComputeUtilizationResult.Unavailable("Windows uses the WDDM CUDA/Compute engine counter for CUDA telemetry.");
                return new NvmlSnapshot
                {
                    Available = true,
                    Name = ReadString(_getName!, 96),
                    UtilizationPercent = Math.Clamp(utilization.Gpu, 0u, 100u),
                    MemoryUsedBytes = memory.Used,
                    MemoryTotalBytes = memory.Total,
                    DriverVersion = ReadString(_getDriverVersion!, 96),
                    CudaDriverVersion = ReadCudaDriverVersion(),
                    StatusMessage = null,
                    ComputeUtilizationAvailable = compute.Available,
                    ComputeUtilizationPercent = compute.Percent,
                    ComputeUtilizationSource = compute.Source,
                    ComputeUtilizationStatusMessage = compute.StatusMessage,
                    ComputeProcessCount = compute.ProcessCount
                };
            }
            catch (Exception ex)
            {
                string error = $"NVML query error: {ex.Message}";
                return new NvmlSnapshot
                {
                    Available = false,
                    ComputeUtilizationAvailable = false,
                    StatusMessage = error,
                    ComputeUtilizationStatusMessage = error
                };
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_initialized && _shutdown is not null)
            {
                try
                {
                    _shutdown();
                }
                catch
                {
                }
            }

            _initialized = false;
            if (_library != IntPtr.Zero)
            {
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
            }
        }
    }

    private void Initialize()
    {
        lock (_gate)
        {
            string[] candidates = OperatingSystem.IsWindows()
                ? ["nvml.dll", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll"]
                : [
                    "libnvidia-ml.so.1",
                    "libnvidia-ml.so",
                    "/usr/lib/x86_64-linux-gnu/libnvidia-ml.so.1",
                    "/usr/lib/aarch64-linux-gnu/libnvidia-ml.so.1",
                    "/usr/lib64/libnvidia-ml.so.1",
                    "/usr/lib/wsl/lib/libnvidia-ml.so.1"
                ];

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (NativeLibrary.TryLoad(candidate, out _library))
                {
                    break;
                }
            }

            if (_library == IntPtr.Zero)
            {
                _initializationError = OperatingSystem.IsLinux()
                    ? "NVIDIA NVML library was not found. A working NVIDIA driver (libnvidia-ml.so.1) is required; installing CUDA Toolkit alone does not provide CUDA utilization telemetry."
                    : "NVIDIA NVML library was not found. Install a supported NVIDIA driver.";
                return;
            }

            try
            {
                _init = Load<NvmlInitDelegate>("nvmlInit_v2", "nvmlInit");
                _shutdown = Load<NvmlShutdownDelegate>("nvmlShutdown");
                _getHandle = Load<NvmlDeviceGetHandleByIndexDelegate>("nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
                _getUtilization = Load<NvmlDeviceGetUtilizationRatesDelegate>("nvmlDeviceGetUtilizationRates");
                _getMemory = Load<NvmlDeviceGetMemoryInfoDelegate>("nvmlDeviceGetMemoryInfo");
                _getName = Load<NvmlDeviceGetNameDelegate>("nvmlDeviceGetName");
                _getDriverVersion = Load<NvmlSystemGetDriverVersionDelegate>("nvmlSystemGetDriverVersion");
                _getCudaDriverVersion = TryLoad<NvmlSystemGetCudaDriverVersionDelegate>(
                    "nvmlSystemGetCudaDriverVersion_v2",
                    "nvmlSystemGetCudaDriverVersion");

                // These two APIs are optional so older drivers can still expose the
                // rest of the GPU telemetry. They are available on Maxwell+ devices
                // in current NVML and allow us to isolate compute-context SM usage.
                _getComputeRunningProcesses = TryLoad<NvmlDeviceGetRunningProcessesDelegate>(
                    "nvmlDeviceGetComputeRunningProcesses_v3",
                    "nvmlDeviceGetComputeRunningProcesses_v2",
                    "nvmlDeviceGetComputeRunningProcesses");
                _getProcessUtilization = TryLoad<NvmlDeviceGetProcessUtilizationDelegate>(
                    "nvmlDeviceGetProcessUtilization");

                int initResult = _init();
                if (initResult != NvmlSuccess)
                {
                    _initializationError = $"nvmlInit returned error {initResult}.";
                    return;
                }

                int handleResult = _getHandle(0, out _device);
                if (handleResult != NvmlSuccess || _device == IntPtr.Zero)
                {
                    _shutdown();
                    _initializationError = $"No NVIDIA GPU could be opened through NVML (error {handleResult}).";
                    return;
                }

                _initialized = true;
            }
            catch (Exception ex)
            {
                _initializationError = $"NVML initialization failed: {ex.Message}";
                if (_library != IntPtr.Zero)
                {
                    NativeLibrary.Free(_library);
                    _library = IntPtr.Zero;
                }
            }
        }
    }

    private ComputeUtilizationResult ReadComputeUtilization()
    {
        if (_getComputeRunningProcesses is null)
        {
            return ComputeUtilizationResult.Unavailable(
                "The NVIDIA driver does not export compute-process enumeration; CUDA-specific utilization cannot be isolated safely.");
        }

        RunningProcessesResult processes = ReadRunningProcesses(_getComputeRunningProcesses);
        if (!processes.Available)
        {
            return ComputeUtilizationResult.Unavailable(processes.StatusMessage ?? "CUDA compute-process enumeration failed.");
        }

        if (processes.ProcessIds.Count == 0)
        {
            return new ComputeUtilizationResult(
                Available: true,
                Percent: 0,
                ProcessCount: 0,
                Source: "NVML CUDA compute-process SM",
                StatusMessage: "No active NVIDIA compute context is currently running.");
        }

        if (_getProcessUtilization is null)
        {
            return ComputeUtilizationResult.Unavailable(
                "The NVIDIA driver does not export per-process SM utilization; device-wide GPU utilization is intentionally not reported as CUDA usage.",
                processes.ProcessIds.Count);
        }

        ProcessUtilizationResult utilization = ReadProcessUtilization(_lastProcessUtilizationTimestamp);
        if (!utilization.Available)
        {
            return ComputeUtilizationResult.Unavailable(
                utilization.StatusMessage ?? "CUDA process SM utilization is unavailable.",
                processes.ProcessIds.Count);
        }

        if (utilization.LatestTimestamp > _lastProcessUtilizationTimestamp)
        {
            _lastProcessUtilizationTimestamp = utilization.LatestTimestamp;
        }

        // NVML's per-process samples expose SM (3D/Compute) utilization. Restrict
        // those samples to PIDs that NVML itself reports as compute contexts. This
        // avoids treating NVDEC/NVENC-only or unrelated graphics activity as CUDA.
        List<NvmlProcessUtilizationSample> computeSamples = utilization.Samples
            .Where(sample => processes.ProcessIds.Contains(sample.Pid))
            .ToList();

        if (computeSamples.Count == 0)
        {
            return new ComputeUtilizationResult(
                Available: true,
                Percent: 0,
                ProcessCount: processes.ProcessIds.Count,
                Source: "NVML CUDA compute-process SM",
                StatusMessage: "CUDA compute contexts are present but no non-zero SM sample was recorded in the latest NVML sample period.");
        }

        ulong newestTimestamp = computeSamples.Max(sample => sample.TimeStamp);
        Dictionary<uint, NvmlProcessUtilizationSample> latestByProcess = new();
        foreach (NvmlProcessUtilizationSample sample in computeSamples)
        {
            if (newestTimestamp > sample.TimeStamp &&
                newestTimestamp - sample.TimeStamp > ProcessSampleFreshnessWindowUs)
            {
                continue;
            }

            if (!latestByProcess.TryGetValue(sample.Pid, out NvmlProcessUtilizationSample existing) ||
                sample.TimeStamp > existing.TimeStamp)
            {
                latestByProcess[sample.Pid] = sample;
            }
        }

        double smUtilization = latestByProcess.Values.Sum(sample => Math.Clamp((double)sample.SmUtil, 0, 100));
        smUtilization = Math.Clamp(smUtilization, 0, 100);

        return new ComputeUtilizationResult(
            Available: true,
            Percent: smUtilization,
            ProcessCount: processes.ProcessIds.Count,
            Source: processes.ProcessIds.Count == 1
                ? "NVML CUDA compute-process SM"
                : $"NVML CUDA compute-process SM · {processes.ProcessIds.Count} processes",
            StatusMessage: "Measured from SM utilization samples for processes that currently own NVIDIA compute contexts; decoder/encoder-only activity is excluded.");
    }

    private RunningProcessesResult ReadRunningProcesses(NvmlDeviceGetRunningProcessesDelegate function)
    {
        uint count = 0;
        int result = function(_device, ref count, IntPtr.Zero);
        if (result == NvmlSuccess && count == 0)
        {
            return new RunningProcessesResult(true, new HashSet<uint>(), null);
        }

        if (result != NvmlErrorInsufficientSize && result != NvmlSuccess)
        {
            return new RunningProcessesResult(false, new HashSet<uint>(), DescribeNvmlError("compute-process query", result));
        }

        if (count == 0)
        {
            return new RunningProcessesResult(true, new HashSet<uint>(), null);
        }

        int itemSize = Marshal.SizeOf<NvmlProcessInfo>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint capacity = checked(count + 8u);
            IntPtr buffer = Marshal.AllocHGlobal(checked(itemSize * (int)capacity));
            try
            {
                uint requested = capacity;
                result = function(_device, ref requested, buffer);
                if (result == NvmlErrorInsufficientSize)
                {
                    count = Math.Max(requested, capacity + 8u);
                    continue;
                }

                if (result != NvmlSuccess)
                {
                    return new RunningProcessesResult(false, new HashSet<uint>(), DescribeNvmlError("compute-process query", result));
                }

                var pids = new HashSet<uint>();
                uint returned = Math.Min(requested, capacity);
                for (uint index = 0; index < returned; index++)
                {
                    IntPtr itemPointer = IntPtr.Add(buffer, checked((int)index * itemSize));
                    NvmlProcessInfo info = Marshal.PtrToStructure<NvmlProcessInfo>(itemPointer);
                    if (info.Pid != 0)
                    {
                        pids.Add(info.Pid);
                    }
                }

                return new RunningProcessesResult(true, pids, null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return new RunningProcessesResult(false, new HashSet<uint>(), "CUDA compute-process list changed too quickly to obtain a stable NVML snapshot.");
    }

    private ProcessUtilizationResult ReadProcessUtilization(ulong lastSeenTimestamp)
    {
        uint count = 0;
        int result = _getProcessUtilization!(_device, IntPtr.Zero, ref count, lastSeenTimestamp);

        if (result == NvmlErrorNotFound || (result == NvmlSuccess && count == 0))
        {
            return new ProcessUtilizationResult(true, Array.Empty<NvmlProcessUtilizationSample>(), lastSeenTimestamp, null);
        }

        if (result != NvmlErrorInsufficientSize && result != NvmlSuccess)
        {
            return new ProcessUtilizationResult(false, Array.Empty<NvmlProcessUtilizationSample>(), lastSeenTimestamp,
                DescribeNvmlError("process-utilization query", result));
        }

        if (count == 0)
        {
            return new ProcessUtilizationResult(true, Array.Empty<NvmlProcessUtilizationSample>(), lastSeenTimestamp, null);
        }

        int itemSize = Marshal.SizeOf<NvmlProcessUtilizationSample>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint capacity = checked(count + 8u);
            IntPtr buffer = Marshal.AllocHGlobal(checked(itemSize * (int)capacity));
            try
            {
                uint requested = capacity;
                result = _getProcessUtilization(_device, buffer, ref requested, lastSeenTimestamp);
                if (result == NvmlErrorInsufficientSize)
                {
                    count = Math.Max(requested, capacity + 8u);
                    continue;
                }

                if (result == NvmlErrorNotFound)
                {
                    return new ProcessUtilizationResult(true, Array.Empty<NvmlProcessUtilizationSample>(), lastSeenTimestamp, null);
                }

                if (result != NvmlSuccess)
                {
                    return new ProcessUtilizationResult(false, Array.Empty<NvmlProcessUtilizationSample>(), lastSeenTimestamp,
                        DescribeNvmlError("process-utilization query", result));
                }

                uint returned = Math.Min(requested, capacity);
                var samples = new List<NvmlProcessUtilizationSample>(checked((int)returned));
                ulong latestTimestamp = lastSeenTimestamp;
                for (uint index = 0; index < returned; index++)
                {
                    IntPtr itemPointer = IntPtr.Add(buffer, checked((int)index * itemSize));
                    NvmlProcessUtilizationSample sample = Marshal.PtrToStructure<NvmlProcessUtilizationSample>(itemPointer);
                    samples.Add(sample);
                    latestTimestamp = Math.Max(latestTimestamp, sample.TimeStamp);
                }

                return new ProcessUtilizationResult(true, samples, latestTimestamp, null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return new ProcessUtilizationResult(false, Array.Empty<NvmlProcessUtilizationSample>(), lastSeenTimestamp,
            "CUDA process utilization changed too quickly to obtain a stable NVML snapshot.");
    }

    private static string DescribeNvmlError(string operation, int code) => code switch
    {
        NvmlErrorNotSupported => $"NVML {operation} is not supported by this GPU/driver.",
        NvmlErrorNoPermission => $"NVML {operation} was denied. Run the management agent with sufficient privileges.",
        NvmlErrorNotFound => $"NVML {operation} returned no matching sample.",
        _ => $"NVML {operation} failed with error {code}."
    };

    private T Load<T>(params string[] names) where T : Delegate
    {
        T? value = TryLoad<T>(names);
        return value ?? throw new MissingMethodException($"Required NVML export not found: {string.Join(" or ", names)}");
    }

    private T? TryLoad<T>(params string[] names) where T : Delegate
    {
        foreach (string name in names)
        {
            if (NativeLibrary.TryGetExport(_library, name, out IntPtr address))
            {
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }
        }
        return null;
    }

    private string? ReadString(NvmlDeviceGetNameDelegate function, int capacity)
    {
        IntPtr buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            Marshal.Copy(new byte[capacity], 0, buffer, capacity);
            int result = function(_device, buffer, (uint)capacity);
            return result == NvmlSuccess ? Marshal.PtrToStringAnsi(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string? ReadString(NvmlSystemGetDriverVersionDelegate function, int capacity)
    {
        IntPtr buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            Marshal.Copy(new byte[capacity], 0, buffer, capacity);
            int result = function(buffer, (uint)capacity);
            return result == NvmlSuccess ? Marshal.PtrToStringAnsi(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string? ReadCudaDriverVersion()
    {
        if (_getCudaDriverVersion is null)
        {
            return null;
        }

        int result = _getCudaDriverVersion(out int rawVersion);
        if (result != NvmlSuccess || rawVersion <= 0)
        {
            return null;
        }

        int major = rawVersion / 1000;
        int minor = (rawVersion % 1000) / 10;
        return $"{major}.{minor}";
    }

    private readonly record struct ComputeUtilizationResult(
        bool Available,
        double Percent,
        int ProcessCount,
        string? Source,
        string? StatusMessage)
    {
        public static ComputeUtilizationResult Unavailable(string message, int processCount = 0) =>
            new(false, 0, processCount, null, message);
    }

    private readonly record struct RunningProcessesResult(
        bool Available,
        HashSet<uint> ProcessIds,
        string? StatusMessage);

    private readonly record struct ProcessUtilizationResult(
        bool Available,
        IReadOnlyList<NvmlProcessUtilizationSample> Samples,
        ulong LatestTimestamp,
        string? StatusMessage);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlProcessInfo
    {
        public uint Pid;
        public ulong UsedGpuMemory;
        public uint GpuInstanceId;
        public uint ComputeInstanceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlProcessUtilizationSample
    {
        public uint Pid;
        public ulong TimeStamp;
        public uint SmUtil;
        public uint MemUtil;
        public uint EncUtil;
        public uint DecUtil;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandleByIndexDelegate(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetUtilizationRatesDelegate(IntPtr device, ref NvmlUtilization utilization);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetMemoryInfoDelegate(IntPtr device, ref NvmlMemory memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetNameDelegate(IntPtr device, IntPtr name, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlSystemGetDriverVersionDelegate(IntPtr version, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlSystemGetCudaDriverVersionDelegate(out int version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetRunningProcessesDelegate(IntPtr device, ref uint infoCount, IntPtr infos);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetProcessUtilizationDelegate(
        IntPtr device,
        IntPtr utilization,
        ref uint processSamplesCount,
        ulong lastSeenTimeStamp);
}
