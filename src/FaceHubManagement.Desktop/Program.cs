using Avalonia;
using FaceHubManagement.Desktop.Diagnostics;

namespace FaceHubManagement.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DesktopDiagnostics.InstallProcessHandlers();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            DesktopDiagnostics.WriteMainFailure(ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        builder = DesktopDiagnostics.ConfigureLinuxRenderer(builder);
        return builder.LogToTrace();
    }
}
