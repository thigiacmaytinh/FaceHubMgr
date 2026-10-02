using System.Text.Json;

namespace FaceHubManagement.Core.Ipc;

public sealed class IpcResponse
{
    public Guid RequestId { get; set; }
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public JsonElement? Payload { get; set; }

    public static IpcResponse Ok<T>(Guid requestId, T payload)
    {
        return new IpcResponse
        {
            RequestId = requestId,
            Success = true,
            Payload = JsonSerializer.SerializeToElement(payload, JsonDefaults.Options)
        };
    }

    public static IpcResponse Error(Guid requestId, string code, string message)
    {
        return new IpcResponse
        {
            RequestId = requestId,
            Success = false,
            ErrorCode = code,
            ErrorMessage = message
        };
    }
}
