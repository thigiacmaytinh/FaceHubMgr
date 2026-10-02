using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FaceHubManagement.Desktop.Views;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog(string title, string message, string detail)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = new ConfirmationDialogModel(title, message, detail);
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private sealed record ConfirmationDialogModel(string TitleText, string MessageText, string DetailText);
}
