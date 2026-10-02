using Avalonia;
using Avalonia.Styling;

namespace FaceHubManagement.Desktop.Services;

public static class ThemeService
{
    public static void Apply(string theme)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
