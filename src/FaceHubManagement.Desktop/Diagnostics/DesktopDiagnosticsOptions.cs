using System.Text.Json;

namespace FaceHubManagement.Desktop.Diagnostics;

internal sealed class DesktopDiagnosticsOptions
{
    private const string SystemConfigPath = "/etc/facehub-management/desktop-diagnostics.json";

    private DesktopDiagnosticsOptions(
        string fontMode,
        string renderer,
        string logPath,
        IReadOnlyList<string> loadedConfigPaths,
        IReadOnlyList<string> warnings)
    {
        FontMode = fontMode;
        Renderer = renderer;
        LogPath = logPath;
        LoadedConfigPaths = loadedConfigPaths;
        Warnings = warnings;
    }

    public string FontMode { get; }
    public string Renderer { get; }
    public string LogPath { get; }
    public IReadOnlyList<string> LoadedConfigPaths { get; }
    public IReadOnlyList<string> Warnings { get; }

    public bool UseSafeFonts => OperatingSystem.IsLinux() && FontMode == "safe";
    public string EffectiveUiFontFamily => UseSafeFonts ? "DejaVu Sans" : "Segoe UI, Ubuntu, Sans";
    public string EffectiveMonoFontFamily => UseSafeFonts ? "DejaVu Sans Mono" : "Consolas, monospace";

    public static DesktopDiagnosticsOptions Load()
    {
        string fontMode = OperatingSystem.IsLinux() ? "safe" : "system";
        string renderer = "auto";
        string? logPath = null;
        var loaded = new List<string>();
        var warnings = new List<string>();

        foreach (string path in GetConfigPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                DiagnosticConfig? config = JsonSerializer.Deserialize<DiagnosticConfig>(
                    File.ReadAllText(path),
                    jsonOptions);

                if (config is not null)
                {
                    fontMode = NormalizeFontMode(config.FontMode, fontMode, warnings, path);
                    renderer = NormalizeRenderer(config.Renderer, renderer, warnings, path);
                    if (!string.IsNullOrWhiteSpace(config.LogPath))
                    {
                        logPath = config.LogPath.Trim();
                    }
                }

                loaded.Add(path);
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to read diagnostics config '{path}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        string? envFontMode = Environment.GetEnvironmentVariable("FACEHUB_DIAG_FONT_MODE");
        if (!string.IsNullOrWhiteSpace(envFontMode))
        {
            fontMode = NormalizeFontMode(envFontMode, fontMode, warnings, "FACEHUB_DIAG_FONT_MODE");
        }

        string? envRenderer = Environment.GetEnvironmentVariable("FACEHUB_DIAG_RENDERER");
        if (!string.IsNullOrWhiteSpace(envRenderer))
        {
            renderer = NormalizeRenderer(envRenderer, renderer, warnings, "FACEHUB_DIAG_RENDERER");
        }

        string? envLogPath = Environment.GetEnvironmentVariable("FACEHUB_DIAG_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(envLogPath))
        {
            logPath = envLogPath.Trim();
        }

        string resolvedLogPath = ResolveLogPath(logPath);
        return new DesktopDiagnosticsOptions(fontMode, renderer, resolvedLogPath, loaded, warnings);
    }

    private static IEnumerable<string> GetConfigPaths()
    {
        if (OperatingSystem.IsLinux())
        {
            yield return SystemConfigPath;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".config", "facehub-management", "desktop-diagnostics.json");
        }
    }

    private static string NormalizeFontMode(
        string? value,
        string fallback,
        ICollection<string> warnings,
        string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string normalized = value.Trim().ToLowerInvariant();
        if (normalized is "safe" or "system")
        {
            return normalized;
        }

        warnings.Add($"Ignoring invalid fontMode '{value}' from {source}; expected 'safe' or 'system'.");
        return fallback;
    }

    private static string NormalizeRenderer(
        string? value,
        string fallback,
        ICollection<string> warnings,
        string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string normalized = value.Trim().ToLowerInvariant();
        if (normalized is "auto" or "glx" or "software")
        {
            return normalized;
        }

        warnings.Add($"Ignoring invalid renderer '{value}' from {source}; expected 'auto', 'glx', or 'software'.");
        return fallback;
    }

    private static string ResolveLogPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return ExpandHome(configuredPath);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "FaceHub Management", "desktop-diagnostics.log");
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".local", "state", "facehub-management", "desktop-diagnostics.log");
        }

        return Path.Combine(Path.GetTempPath(), "facehub-management-desktop-diagnostics.log");
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[2..]);
        }

        return path;
    }

    private sealed class DiagnosticConfig
    {
        public string? FontMode { get; set; }
        public string? Renderer { get; set; }
        public string? LogPath { get; set; }
    }
}
