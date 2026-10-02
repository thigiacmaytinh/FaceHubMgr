namespace FaceHubManagement.Agent.Ipc;

public interface IIpcServer
{
    Task RunAsync(Func<Stream, CancellationToken, Task> handleClientAsync, CancellationToken cancellationToken);
}
