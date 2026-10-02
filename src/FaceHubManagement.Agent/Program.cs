using System.Runtime.InteropServices;
using FaceHubManagement.Agent.Platform;

namespace FaceHubManagement.Agent;

public static class Program
{
    private const string ServiceArgument = "--service";
    private const string ConsoleArgument = "--console";

    public static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            return RunWindows(args);
        }

        if (OperatingSystem.IsLinux())
        {
            return RunLinux(args);
        }

        Console.Error.WriteLine("Unsupported operating system.");
        return 2;
    }

    private static int RunWindows(string[] args)
    {
        if (HasArgument(args, ServiceArgument))
        {
            try
            {
                WindowsServiceHost.Run(
                    "FaceHubManagementAgent",
                    RunAgentAsync);

                return 0;
            }
            catch
            {
                return 1;
            }
        }

        if (HasArgument(args, ConsoleArgument))
        {
            return RunConsoleWithErrorHandling();
        }

        // Production Windows behavior:
        // Running/double-clicking Agent.exe without an explicit mode
        // must NOT start a long-running console agent.
        return 0;
    }

    private static int RunLinux(string[] args)
    {
        // systemd currently starts the agent without arguments:
        //
        // ExecStart=/opt/facehub-management/agent/FaceHubManagement.Agent
        //
        // Therefore no arguments must continue to mean
        // foreground daemon mode on Linux.
        if (args.Length == 0 ||
            HasArgument(args, ConsoleArgument))
        {
            return RunConsoleWithErrorHandling();
        }

        Console.Error.WriteLine(
            $"Unsupported argument: {string.Join(' ', args)}");

        return 2;
    }

    private static int RunConsoleWithErrorHandling()
    {
        try
        {
            return RunConsoleAsync()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static bool HasArgument(
        IEnumerable<string> args,
        string argument)
    {
        return args.Contains(
            argument,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<int> RunConsoleAsync()
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += OnCancelKeyPress;

        using PosixSignalRegistration? sigTerm =
            OperatingSystem.IsLinux()
                ? PosixSignalRegistration.Create(
                    PosixSignal.SIGTERM,
                    context =>
                    {
                        context.Cancel = true;
                        cts.Cancel();
                    })
                : null;

        try
        {
            await RunAgentAsync(cts.Token)
                .ConfigureAwait(false);

            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        void OnCancelKeyPress(
            object? sender,
            ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        }
    }

    private static async Task RunAgentAsync(
        CancellationToken cancellationToken)
    {
        using var runtime = new AgentRuntime();

        await runtime
            .RunAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}