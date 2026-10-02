namespace FaceHubManagement.Core.Ipc;

public sealed class AgentClientException : Exception
{
    public string Code { get; }

    public AgentClientException(string code, string message) : base(message)
    {
        Code = code;
    }

    public AgentClientException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}
