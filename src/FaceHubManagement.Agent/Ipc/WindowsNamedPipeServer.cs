using System.IO.Pipes;

namespace FaceHubManagement.Agent.Ipc;

public sealed class WindowsNamedPipeServer : IIpcServer
{
    private const int ListenerCount = 16;
    private readonly string _pipeName;

    public WindowsNamedPipeServer(string pipeName)
    {
        _pipeName = pipeName;
    }

    public async Task RunAsync(Func<Stream, CancellationToken, Task> handleClientAsync, CancellationToken cancellationToken)
    {
        // Keep several named-pipe instances waiting at all times. The desktop can issue
        // telemetry, heartbeat, logs and firewall requests concurrently; a single slow
        // request must never prevent new clients from connecting.
        Task[] listeners = Enumerable
            .Range(0, ListenerCount)
            .Select(_ => RunListenerAsync(handleClientAsync, cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunListenerAsync(
        Func<Stream, CancellationToken, Task> handleClientAsync,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = WindowsNamedPipeFactory.Create(_pipeName);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await handleClientAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A malformed/disconnected client must only terminate this request.
                // Keep the listener alive for the next desktop connection.
            }
        }
    }
}
