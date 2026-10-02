using System.Text.Json;
using FaceHubManagement.Core.Ipc;

namespace FaceHubManagement.Desktop.Services;

public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore()
    {
        _path = ResolvePath();
    }

    public DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new DesktopSettings();
            }

            DesktopSettings? settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(_path), JsonDefaults.Options);
            return Normalize(settings ?? new DesktopSettings());
        }
        catch
        {
            return new DesktopSettings();
        }
    }

    public async Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        DesktopSettings normalized = Normalize(settings);
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true };
        string json = JsonSerializer.Serialize(normalized, options);
        string temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, overwrite: true);
    }

    private static DesktopSettings Normalize(DesktopSettings settings)
    {
        settings.Theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "System";
        settings.ResourceRefreshMilliseconds = Math.Clamp(settings.ResourceRefreshMilliseconds, 500, 5000);
        settings.ServiceRefreshMilliseconds = Math.Clamp(settings.ServiceRefreshMilliseconds, 1000, 10000);
        settings.LogRefreshMilliseconds = Math.Clamp(settings.LogRefreshMilliseconds, 1000, 10000);
        settings.WindowsPipeName = string.IsNullOrWhiteSpace(settings.WindowsPipeName)
            ? "FaceHubManagement.Agent"
            : settings.WindowsPipeName.Trim();
        settings.LinuxSocketPath = string.IsNullOrWhiteSpace(settings.LinuxSocketPath)
            ? "/run/facehub-management/agent.sock"
            : settings.LinuxSocketPath.Trim();
        return settings;
    }

    private static string ResolvePath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FaceHub Management",
                "settings.json");
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "facehub-management", "settings.json");
    }
}
