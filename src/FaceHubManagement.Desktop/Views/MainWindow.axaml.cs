using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FaceHubManagement.Desktop.ViewModels;

namespace FaceHubManagement.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly Grid? _resourceCardsGrid;
    private readonly Border? _cpuCard;
    private readonly Border? _ramCard;
    private readonly Border? _diskCard;
    private readonly Border? _cudaCard;
    private readonly Border? _vramCard;
    private bool? _compactMetricsLayout;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _resourceCardsGrid = this.FindControl<Grid>("ResourceCardsGrid");
        _cpuCard = this.FindControl<Border>("CpuCard");
        _ramCard = this.FindControl<Border>("RamCard");
        _diskCard = this.FindControl<Border>("DiskCard");
        _cudaCard = this.FindControl<Border>("CudaCard");
        _vramCard = this.FindControl<Border>("VramCard");

        Opened += (_, _) =>
        {
            UpdateResponsiveLayout();
            (DataContext as MainWindowViewModel)?.Start();
        };
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Closing += (_, _) => (DataContext as MainWindowViewModel)?.Stop();
    }

    private void UpdateResponsiveLayout()
    {
        if (_resourceCardsGrid is null ||
            _cpuCard is null ||
            _ramCard is null ||
            _diskCard is null ||
            _cudaCard is null ||
            _vramCard is null)
        {
            return;
        }

        // Five metric cards fit comfortably in one row at the normal desktop width.
        // On narrower windows use a 3+2 layout so labels and progress bars stay readable.
        bool compact = ClientSize.Width < 1280;
        if (_compactMetricsLayout == compact)
        {
            return;
        }

        _compactMetricsLayout = compact;
        _resourceCardsGrid.ColumnDefinitions.Clear();
        _resourceCardsGrid.RowDefinitions.Clear();

        if (compact)
        {
            for (int index = 0; index < 3; index++)
            {
                _resourceCardsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            }
            _resourceCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _resourceCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            Place(_cpuCard, 0, 0);
            Place(_ramCard, 0, 1);
            Place(_diskCard, 0, 2);
            Place(_cudaCard, 1, 0);
            Place(_vramCard, 1, 1);
        }
        else
        {
            for (int index = 0; index < 5; index++)
            {
                _resourceCardsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            }
            _resourceCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            Place(_cpuCard, 0, 0);
            Place(_ramCard, 0, 1);
            Place(_diskCard, 0, 2);
            Place(_cudaCard, 0, 3);
            Place(_vramCard, 0, 4);
        }
    }

    private async void AddFirewallPort_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new AddPortDialog();
        AddPortDialogResult? result = await dialog.ShowDialog<AddPortDialogResult?>(this);
        if (result is not null && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.AddFirewallPortAsync(result.Port, result.Name);
        }
    }

    private async void EnableAllFirewallPorts_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.SetAllFirewallPortsAsync(true);
        }
    }

    private async void DisableAllFirewallPorts_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new ConfirmationDialog(
            "Disable all listed firewall ports?",
            "This will set every listed port to Deny using the system firewall.",
            "On Ubuntu, simple/global UFW rules for each listed TCP port are replaced with DENY rules and UFW is reloaded. Source-IP/subnet-specific rules are preserved.");
        bool confirmed = await dialog.ShowDialog<bool>(this);
        if (confirmed)
        {
            await viewModel.SetAllFirewallPortsAsync(false);
        }
    }

    private async void EnableFirewall_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.SetFirewallEnabledAsync(true);
        }
    }

    private async void DisableFirewall_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new ConfirmationDialog(
            "Disable firewall?",
            "This will disable the host firewall for all active profiles.",
            "Existing rules are preserved, but they will not protect the machine while the firewall is disabled.");
        bool confirmed = await dialog.ShowDialog<bool>(this);
        if (confirmed)
        {
            await viewModel.SetFirewallEnabledAsync(false);
        }
    }

    private async void LogRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: LogItemViewModel log })
        {
            return;
        }

        e.Handled = true;
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new LogDetailsDialog(
            log,
            cancellationToken => viewModel.GetServiceDiagnosticsForLogAsync(log, cancellationToken));
        await dialog.ShowDialog(this);
    }

    private static void Place(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
    }
}
