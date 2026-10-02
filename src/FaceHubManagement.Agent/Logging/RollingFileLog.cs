using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Logging;

public sealed class RollingFileLog
{
    private readonly string _directory;
    private readonly string _currentPath;
    private readonly int _maxFileBytes;
    private readonly int _retainedFiles;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RollingFileLog(string directory, int maxFileBytes, int retainedFiles)
    {
        _directory = directory;
        _currentPath = Path.Combine(directory, "facehub-management.log");
        _maxFileBytes = maxFileBytes;
        _retainedFiles = retainedFiles;
        Directory.CreateDirectory(_directory);
    }

    public async Task InfoAsync(string component, string message, CancellationToken cancellationToken = default) =>
        await WriteAsync("INFO", component, message, null, null, cancellationToken).ConfigureAwait(false);

    public async Task WarningAsync(string component, string message, CancellationToken cancellationToken = default) =>
        await WriteAsync("WARN", component, message, null, null, cancellationToken).ConfigureAwait(false);

    public async Task ErrorAsync(string component, string message, CancellationToken cancellationToken = default) =>
        await WriteAsync("ERROR", component, message, null, null, cancellationToken).ConfigureAwait(false);

    public async Task ServiceEventAsync(
        string level,
        string serviceId,
        string operation,
        string message,
        CancellationToken cancellationToken = default) =>
        await WriteAsync(level, "Service", message, serviceId, operation, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ServiceLogEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lines = new List<string>();
            for (int index = _retainedFiles; index >= 1; index--)
            {
                string path = $"{_currentPath}.{index}";
                if (File.Exists(path))
                {
                    lines.AddRange(await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false));
                }
            }

            if (File.Exists(_currentPath))
            {
                lines.AddRange(await File.ReadAllLinesAsync(_currentPath, cancellationToken).ConfigureAwait(false));
            }

            return lines
                .TakeLast(limit)
                .Reverse()
                .Select(Parse)
                .Where(entry => entry is not null)
                .Cast<ServiceLogEntry>()
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteAsync(
        string level,
        string component,
        string message,
        string? serviceId,
        string? operation,
        CancellationToken cancellationToken)
    {
        string sanitized = Sanitize(message);
        string sanitizedServiceId = Sanitize(serviceId ?? string.Empty);
        string sanitizedOperation = Sanitize(operation ?? string.Empty);
        // v2 format keeps service metadata for on-demand diagnostics. The parser remains
        // backward compatible with the original four-field records.
        string line = $"{DateTimeOffset.UtcNow:O}|{level}|{component}|{sanitizedServiceId}|{sanitizedOperation}|{sanitized}{Environment.NewLine}";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RotateIfNeeded();
            await File.AppendAllTextAsync(_currentPath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Sanitize(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Replace('|', '/');

    private void RotateIfNeeded()
    {
        if (!File.Exists(_currentPath) || new FileInfo(_currentPath).Length < _maxFileBytes)
        {
            return;
        }

        string oldest = $"{_currentPath}.{_retainedFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int index = _retainedFiles - 1; index >= 1; index--)
        {
            string source = $"{_currentPath}.{index}";
            string destination = $"{_currentPath}.{index + 1}";
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        File.Move(_currentPath, $"{_currentPath}.1", overwrite: true);
    }

    private static ServiceLogEntry? Parse(string line)
    {
        string[] v2 = line.Split('|', 6);
        if (v2.Length == 6 && DateTimeOffset.TryParse(v2[0], out DateTimeOffset v2Timestamp))
        {
            return new ServiceLogEntry
            {
                Timestamp = v2Timestamp,
                Level = v2[1],
                Component = v2[2],
                ServiceId = string.IsNullOrWhiteSpace(v2[3]) ? null : v2[3],
                Operation = string.IsNullOrWhiteSpace(v2[4]) ? null : v2[4],
                Message = v2[5]
            };
        }

        string[] legacy = line.Split('|', 4);
        if (legacy.Length != 4 || !DateTimeOffset.TryParse(legacy[0], out DateTimeOffset timestamp))
        {
            return null;
        }

        return new ServiceLogEntry
        {
            Timestamp = timestamp,
            Level = legacy[1],
            Component = legacy[2],
            Message = legacy[3]
        };
    }
}
