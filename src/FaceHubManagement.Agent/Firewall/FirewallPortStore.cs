using System.Text.Json;
using FaceHubManagement.Agent.Configuration;
using FaceHubManagement.Core.Ipc;

namespace FaceHubManagement.Agent.Firewall;

public sealed class FirewallPortStore
{
    private readonly string _path;
    private readonly IReadOnlyList<MonitoredPortDefinition> _defaults;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FirewallPortStore(string path, IEnumerable<MonitoredPortDefinition> defaults)
    {
        _path = path;
        _defaults = defaults
            .Where(item => item.Port is >= 1 and <= 65535)
            .GroupBy(item => item.Port)
            .Select(group => new MonitoredPortDefinition
            {
                Port = group.Key,
                Name = string.IsNullOrWhiteSpace(group.First().Name) ? $"TCP {group.Key}" : group.First().Name.Trim()
            })
            .OrderBy(item => item.Port)
            .ToArray();
    }

    public async Task<IReadOnlyList<MonitoredPortDefinition>> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MonitoredPortDefinition> AddAsync(int port, string? name, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<MonitoredPortDefinition> items = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            MonitoredPortDefinition? existing = items.FirstOrDefault(item => item.Port == port);
            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    existing.Name = name.Trim();
                    await SaveCoreAsync(items, cancellationToken).ConfigureAwait(false);
                }
                return Clone(existing);
            }

            var added = new MonitoredPortDefinition
            {
                Port = port,
                Name = string.IsNullOrWhiteSpace(name) ? $"TCP {port}" : name.Trim()
            };
            items.Add(added);
            items.Sort((left, right) => left.Port.CompareTo(right.Port));
            await SaveCoreAsync(items, cancellationToken).ConfigureAwait(false);
            return Clone(added);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MonitoredPortDefinition> GetRequiredAsync(int port, CancellationToken cancellationToken)
    {
        IReadOnlyList<MonitoredPortDefinition> items = await GetAsync(cancellationToken).ConfigureAwait(false);
        return items.FirstOrDefault(item => item.Port == port)
               ?? throw new KeyNotFoundException($"Port {port}/TCP is not in the managed firewall list.");
    }

    private async Task<IReadOnlyList<MonitoredPortDefinition>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            var seed = _defaults.Select(Clone).ToList();
            await SaveCoreAsync(seed, cancellationToken).ConfigureAwait(false);
            return seed;
        }

        try
        {
            string json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            List<MonitoredPortDefinition>? items = JsonSerializer.Deserialize<List<MonitoredPortDefinition>>(json, JsonDefaults.Options);
            if (items is null)
            {
                throw new JsonException("Firewall port state is empty.");
            }

            return items
                .Where(item => item.Port is >= 1 and <= 65535)
                .GroupBy(item => item.Port)
                .Select(group => Clone(group.First()))
                .OrderBy(item => item.Port)
                .ToArray();
        }
        catch (JsonException)
        {
            // Preserve a malformed file for diagnostics and recover with safe defaults.
            string backup = _path + ".invalid-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(_path, backup, overwrite: false); } catch { }
            var seed = _defaults.Select(Clone).ToList();
            await SaveCoreAsync(seed, cancellationToken).ConfigureAwait(false);
            return seed;
        }
    }

    private async Task SaveCoreAsync(IEnumerable<MonitoredPortDefinition> items, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = _path + ".tmp";
        string json = JsonSerializer.Serialize(items, JsonDefaults.Options);
        await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, overwrite: true);
    }

    private static MonitoredPortDefinition Clone(MonitoredPortDefinition item) => new()
    {
        Port = item.Port,
        Name = string.IsNullOrWhiteSpace(item.Name) ? $"TCP {item.Port}" : item.Name.Trim()
    };
}
