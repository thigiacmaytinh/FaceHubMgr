using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FaceHubManagement.Agent.Platform;

public static class WindowsServiceHost
{
    private const int ServiceControlStop = 0x00000001;
    private const int ServiceControlShutdown = 0x00000005;
    private const int ServiceAcceptStop = 0x00000001;
    private const int ServiceAcceptShutdown = 0x00000004;
    private const int ServiceStopped = 0x00000001;
    private const int ServiceStartPending = 0x00000002;
    private const int ServiceStopPending = 0x00000003;
    private const int ServiceRunning = 0x00000004;
    private const int ServiceWin32OwnProcess = 0x00000010;

    private static string _serviceName = string.Empty;
    private static Func<CancellationToken, Task>? _runner;
    private static CancellationTokenSource? _cts;
    private static IntPtr _statusHandle;
    private static ServiceMainDelegate? _serviceMain;
    private static HandlerExDelegate? _handler;
    private static ServiceStatus _status;

    public static void Run(string serviceName, Func<CancellationToken, Task> runner)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows service hosting is available only on Windows.");
        }

        _serviceName = serviceName;
        _runner = runner;
        _serviceMain = ServiceMain;
        _handler = HandlerEx;

        ServiceTableEntry[] table =
        [
            new ServiceTableEntry { ServiceName = _serviceName, ServiceProc = _serviceMain },
            new ServiceTableEntry { ServiceName = null, ServiceProc = null }
        ];

        if (!StartServiceCtrlDispatcher(table))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "StartServiceCtrlDispatcher failed.");
        }
    }

    private static void ServiceMain(int argCount, IntPtr argPointer)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(_serviceName, _handler!, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        SetStatus(ServiceStartPending, 0, waitHint: 10000);
        SetStatus(ServiceRunning, ServiceAcceptStop | ServiceAcceptShutdown, waitHint: 0);

        try
        {
            _runner!(_cts.Token).GetAwaiter().GetResult();
            SetStatus(ServiceStopped, 0, waitHint: 0);
        }
        catch
        {
            SetStatus(ServiceStopped, 0, waitHint: 0, win32ExitCode: 1);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private static int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is ServiceControlStop or ServiceControlShutdown)
        {
            SetStatus(ServiceStopPending, 0, waitHint: 15000);
            _cts?.Cancel();
        }
        return 0;
    }

    private static void SetStatus(int currentState, int acceptedControls, int waitHint, int win32ExitCode = 0)
    {
        _status.ServiceType = ServiceWin32OwnProcess;
        _status.CurrentState = currentState;
        _status.ControlsAccepted = acceptedControls;
        _status.Win32ExitCode = win32ExitCode;
        _status.ServiceSpecificExitCode = 0;
        _status.CheckPoint = currentState is ServiceStartPending or ServiceStopPending ? _status.CheckPoint + 1 : 0;
        _status.WaitHint = waitHint;
        SetServiceStatus(_statusHandle, ref _status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServiceName;
        public ServiceMainDelegate? ServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(int argCount, IntPtr argPointer);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerExDelegate(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceStartTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(
        string serviceName,
        HandlerExDelegate handlerProc,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus serviceStatus);
}
