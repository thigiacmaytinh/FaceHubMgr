using System.ComponentModel;
using System.Runtime.InteropServices;
using FaceHubManagement.Agent.Configuration;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Services;

public sealed class WindowsServiceManager : ManagedServiceManagerBase
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const int ScStatusProcessInfo = 0;

    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceContinuePending = 0x00000005;
    private const uint ServicePausePending = 0x00000006;
    private const uint ServicePaused = 0x00000007;

    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    public WindowsServiceManager(IEnumerable<ManagedServiceDefinition> definitions) : base(definitions)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
    }

    protected override Task<ManagedServiceInfo> QueryAsync(
        ManagedServiceDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Query(definition));
    }

    protected override async Task ExecuteOperationAsync(
        ManagedServiceDefinition definition,
        ServiceOperation operation,
        CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case ServiceOperation.Start:
                await StartAsync(definition, cancellationToken).ConfigureAwait(false);
                break;
            case ServiceOperation.Stop:
                await StopAsync(definition, cancellationToken).ConfigureAwait(false);
                break;
            case ServiceOperation.Restart:
                ManagedServiceInfo current = Query(definition);
                if (current.State != ServiceState.Stopped)
                {
                    await StopAsync(definition, cancellationToken).ConfigureAwait(false);
                }
                await StartAsync(definition, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private ManagedServiceInfo Query(ManagedServiceDefinition definition)
    {
        IntPtr scm = OpenScManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the Windows Service Control Manager.");
        }

        try
        {
            IntPtr service = OpenService(scm, definition.WindowsServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    return CreateInfo(
                        definition,
                        ServiceState.Failed,
                        null,
                        null,
                        $"Windows service '{definition.WindowsServiceName}' was not found.");
                }

                throw new Win32Exception(error, $"Unable to query Windows service '{definition.WindowsServiceName}'.");
            }

            try
            {
                ServiceStatusProcess status = QueryStatus(service);
                int? pid = status.ProcessId > 0 ? checked((int)status.ProcessId) : null;
                ServiceState state = MapState(status.CurrentState);
                return CreateInfo(definition, state, pid, GetProcessUptimeSeconds(pid), null);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private async Task StartAsync(ManagedServiceDefinition definition, CancellationToken cancellationToken)
    {
        ManagedServiceInfo current = Query(definition);
        if (current.State == ServiceState.Running)
        {
            return;
        }
        if (current.State == ServiceState.Failed && current.Detail?.Contains("was not found", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException(current.Detail);
        }

        WithServiceHandle(definition, ServiceStart | ServiceQueryStatus, service =>
        {
            if (!StartService(service, 0, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceAlreadyRunning)
                {
                    throw new Win32Exception(error, $"Failed to start {definition.DisplayName}.");
                }
            }
        });

        await WaitForStateAsync(definition, ServiceState.Running, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopAsync(ManagedServiceDefinition definition, CancellationToken cancellationToken)
    {
        ManagedServiceInfo current = Query(definition);
        if (current.State == ServiceState.Stopped)
        {
            return;
        }
        if (current.State == ServiceState.Failed && current.Detail?.Contains("was not found", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException(current.Detail);
        }

        WithServiceHandle(definition, ServiceStop | ServiceQueryStatus, service =>
        {
            if (!ControlService(service, ServiceControlStop, out _))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceNotActive)
                {
                    throw new Win32Exception(error, $"Failed to stop {definition.DisplayName}.");
                }
            }
        });

        await WaitForStateAsync(definition, ServiceState.Stopped, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForStateAsync(
        ManagedServiceDefinition definition,
        ServiceState target,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(StateTimeout);

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            ManagedServiceInfo current = Query(definition);
            if (current.State == target)
            {
                return;
            }

            if (current.State == ServiceState.Failed)
            {
                throw new InvalidOperationException(
                    current.Detail ?? $"{definition.DisplayName} entered a failed state while waiting for {target}.");
            }

            await Task.Delay(350, timeoutCts.Token).ConfigureAwait(false);
        }
    }

    private static void WithServiceHandle(
        ManagedServiceDefinition definition,
        uint desiredAccess,
        Action<IntPtr> action)
    {
        IntPtr scm = OpenScManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the Windows Service Control Manager.");
        }

        try
        {
            IntPtr service = OpenService(scm, definition.WindowsServiceName, desiredAccess);
            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    throw new InvalidOperationException($"Windows service '{definition.WindowsServiceName}' was not found.");
                }

                throw new Win32Exception(error, $"Unable to open Windows service '{definition.WindowsServiceName}'.");
            }

            try
            {
                action(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static ServiceStatusProcess QueryStatus(IntPtr service)
    {
        int size = Marshal.SizeOf<ServiceStatusProcess>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryServiceStatusEx failed.");
            }

            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ServiceState MapState(uint state) => state switch
    {
        ServiceRunning => ServiceState.Running,
        ServiceStartPending or ServiceContinuePending => ServiceState.Starting,
        ServiceStopPending => ServiceState.Stopping,
        ServiceStopped or ServicePaused or ServicePausePending => ServiceState.Stopped,
        _ => ServiceState.Unknown
    };

    private static ManagedServiceInfo CreateInfo(
        ManagedServiceDefinition definition,
        ServiceState state,
        int? pid,
        long? uptime,
        string? detail)
    {
        return new ManagedServiceInfo
        {
            Id = definition.Id,
            Name = definition.DisplayName,
            Description = definition.Description,
            State = state,
            ProcessId = pid,
            UptimeSeconds = uptime,
            Ports = definition.Ports,
            Detail = detail
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenScManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr serviceControlManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, int argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        IntPtr buffer,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
