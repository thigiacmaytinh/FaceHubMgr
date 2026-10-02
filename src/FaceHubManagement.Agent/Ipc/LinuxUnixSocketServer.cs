using System.Net.Sockets;

namespace FaceHubManagement.Agent.Ipc;

public sealed class LinuxUnixSocketServer : IIpcServer
{
    private readonly string _socketPath;

    public LinuxUnixSocketServer(string socketPath)
    {
        _socketPath = socketPath;
    }

    public async Task RunAsync(Func<Stream, CancellationToken, Task> handleClientAsync, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("LinuxUnixSocketServer is only supported on Linux.");
        }

        string? directory = Path.GetDirectoryName(_socketPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Unix socket path must include a directory.");
        }

        Directory.CreateDirectory(directory);
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        File.SetUnixFileMode(
            _socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        listener.Listen(32);

        var clients = new HashSet<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                Task clientTask = Task.Run(async () =>
                {
                    await using var stream = new NetworkStream(socket, ownsSocket: true);
                    await handleClientAsync(stream, cancellationToken).ConfigureAwait(false);
                }, CancellationToken.None);

                clients.Add(clientTask);
                clients.RemoveWhere(task => task.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                listener.Close();
            }
            catch
            {
            }

            if (clients.Count > 0)
            {
                try
                {
                    await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }

            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
    }
}
