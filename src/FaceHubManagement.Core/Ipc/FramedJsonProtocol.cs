using System.Buffers.Binary;
using System.Text.Json;

namespace FaceHubManagement.Core.Ipc;

public static class FramedJsonProtocol
{
    public const int MaxFrameBytes = 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        if (payload.Length <= 0 || payload.Length > MaxFrameBytes)
        {
            throw new InvalidDataException($"IPC frame size {payload.Length} is outside the allowed range.");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"IPC frame length {length} is invalid.");
        }

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        T? value = JsonSerializer.Deserialize<T>(payload, JsonDefaults.Options);
        return value ?? throw new InvalidDataException("IPC payload could not be deserialized.");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("IPC stream closed before a complete frame was received.");
            }
            offset += read;
        }
    }
}
