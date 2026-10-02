using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace FaceHubManagement.Desktop.Diagnostics;

internal static class DesktopDiagnostics
{
    private static readonly object Sync = new();
    private static int _processHandlersInstalled;
    private static int _dispatcherHandlersInstalled;

    public static DesktopDiagnosticsOptions Options { get; } = DesktopDiagnosticsOptions.Load();

    public static void InstallProcessHandlers()
    {
        if (Interlocked.Exchange(ref _processHandlersInstalled, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                WriteException("domain-unhandled", exception, $"terminating={args.IsTerminating}");
            }
            else
            {
                Write("domain-unhandled", $"terminating={args.IsTerminating}; object={args.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteException("task-unobserved", args.Exception);
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write("process-exit", "ProcessExit received.");

        Write("startup", BuildStartupSummary());
        foreach (string warning in Options.Warnings)
        {
            Write("config-warning", warning);
        }
    }

    public static AppBuilder ConfigureLinuxRenderer(AppBuilder builder)
    {
        if (!OperatingSystem.IsLinux())
        {
            return builder;
        }

        return Options.Renderer switch
        {
            "software" => builder.With(new X11PlatformOptions
            {
                RenderingMode = new[] { X11RenderingMode.Software }
            }),
            "glx" => builder.With(new X11PlatformOptions
            {
                RenderingMode = new[] { X11RenderingMode.Glx, X11RenderingMode.Software }
            }),
            _ => builder
        };
    }

    public static void ApplyFontResources(Application application)
    {
        if (!Options.UseSafeFonts)
        {
            Write(
                "fonts",
                $"mode=system; ui='{Options.EffectiveUiFontFamily}'; mono='{Options.EffectiveMonoFontFamily}'");
            return;
        }

        application.Resources["AppUiFontFamily"] = new FontFamily(Options.EffectiveUiFontFamily);
        application.Resources["AppMonoFontFamily"] = new FontFamily(Options.EffectiveMonoFontFamily);
        Write(
            "fonts",
            $"mode=safe; ui='{Options.EffectiveUiFontFamily}'; mono='{Options.EffectiveMonoFontFamily}'");
    }

    public static void InstallDispatcherHandlers()
    {
        if (Interlocked.Exchange(ref _dispatcherHandlersInstalled, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            WriteException("ui-unhandled", args.Exception);
            // Diagnostics only: do not set Handled. Preserve the application's existing failure behavior.
        };
    }

    public static void WriteMainFailure(Exception exception) => WriteException("main-fatal", exception);

    public static void Write(string category, string message)
    {
        string line = $"{DateTimeOffset.Now:O} [{category}] {message}";

        try
        {
            lock (Sync)
            {
                string? directory = Path.GetDirectoryName(Options.LogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(Options.LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never bring down the desktop process.
        }

        try
        {
            Trace.WriteLine(line);
            Console.Error.WriteLine(line);
        }
        catch
        {
            // WinExe and detached desktop launches may not have an attached console.
        }
    }

    private static void WriteException(string category, Exception exception, string? suffix = null)
    {
        string message = string.IsNullOrWhiteSpace(suffix)
            ? exception.ToString()
            : $"{suffix}{Environment.NewLine}{exception}";
        Write(category, message);
    }

    private static string BuildStartupSummary()
    {
        string configPaths = Options.LoadedConfigPaths.Count == 0
            ? "<defaults/env only>"
            : string.Join(",", Options.LoadedConfigPaths);

        return string.Join(
            "; ",
            $"pid={Environment.ProcessId}",
            $"app={GetEntryAssemblyVersion()}",
            $"framework='{RuntimeInformation.FrameworkDescription}'",
            $"os='{RuntimeInformation.OSDescription}'",
            $"arch={RuntimeInformation.ProcessArchitecture}",
            $"avalonia={typeof(Application).Assembly.GetName().Version}",
            $"skiaSharp={GetAssemblyFileVersion("SkiaSharp.dll")}",
            $"harfBuzzSharp={GetAssemblyFileVersion("HarfBuzzSharp.dll")}",
            $"fontMode={Options.FontMode}",
            $"renderer={Options.Renderer}",
            $"uiFont='{Options.EffectiveUiFontFamily}'",
            $"monoFont='{Options.EffectiveMonoFontFamily}'",
            $"sessionType='{Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? string.Empty}'",
            $"desktop='{Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty}'",
            $"display='{Environment.GetEnvironmentVariable("DISPLAY") ?? string.Empty}'",
            $"waylandDisplay='{Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? string.Empty}'",
            $"config='{configPaths}'",
            $"log='{Options.LogPath}'");
    }

    private static string GetEntryAssemblyVersion()
    {
        AssemblyName? name = Assembly.GetEntryAssembly()?.GetName();
        return name is null ? "unknown" : $"{name.Name}/{name.Version}";
    }

    private static string GetAssemblyFileVersion(string fileName)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                return "not-found";
            }

            return AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "unknown";
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }
}
