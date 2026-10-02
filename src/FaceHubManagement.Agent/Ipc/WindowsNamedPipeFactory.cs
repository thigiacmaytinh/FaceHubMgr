using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FaceHubManagement.Agent.Ipc;

internal static class WindowsNamedPipeFactory
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint PipeUnlimitedInstances = 255;
    private const uint SddlRevision1 = 1;

    // LocalSystem + built-in Administrators: full access.
    // Members of the local built-in Users group: read/write. Remote clients are rejected by pipe mode.
    private const string PipeSddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;BU)";

    public static NamedPipeServerStream Create(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.IndexOfAny(['\\', '/']) >= 0)
        {
            throw new ArgumentException("Pipe name must be a non-empty local pipe name without path separators.", nameof(pipeName));
        }

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                PipeSddl,
                SddlRevision1,
                out IntPtr securityDescriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the named-pipe security descriptor.");
        }

        try
        {
            var securityAttributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = 0
            };

            string fullName = $@"\\.\pipe\{pipeName}";
            SafePipeHandle handle = CreateNamedPipe(
                fullName,
                PipeAccessDuplex | FileFlagOverlapped,
                PipeRejectRemoteClients,
                PipeUnlimitedInstances,
                64 * 1024,
                64 * 1024,
                0,
                ref securityAttributes);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, $"Unable to create named pipe '{fullName}'.");
            }

            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        ref SecurityAttributes lpSecurityAttributes);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
