using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FaceHubManagement.Desktop.Views;

public sealed record AddPortDialogResult(int Port, string? Name);

public partial class AddPortDialog : Window
{
    private readonly TextBox _portTextBox;
    private readonly TextBox _nameTextBox;
    private readonly TextBlock _errorTextBlock;

    public AddPortDialog()
    {
        AvaloniaXamlLoader.Load(this);
        _portTextBox = this.FindControl<TextBox>("PortTextBox")!;
        _nameTextBox = this.FindControl<TextBox>("NameTextBox")!;
        _errorTextBlock = this.FindControl<TextBlock>("ErrorTextBlock")!;
        Opened += (_, _) => _portTextBox.Focus();
    }

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_portTextBox.Text, out int port) || port is < 1 or > 65535)
        {
            _errorTextBlock.Text = "Enter a valid TCP port between 1 and 65535.";
            _errorTextBlock.IsVisible = true;
            return;
        }

        string? name = string.IsNullOrWhiteSpace(_nameTextBox.Text) ? null : _nameTextBox.Text.Trim();
        Close(new AddPortDialogResult(port, name));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
