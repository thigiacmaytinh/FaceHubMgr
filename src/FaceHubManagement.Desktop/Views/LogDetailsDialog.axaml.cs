using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FaceHubManagement.Core.Models;
using FaceHubManagement.Desktop.ViewModels;

namespace FaceHubManagement.Desktop.Views;

public partial class LogDetailsDialog : Window
{
    private readonly LogDetailsViewModel _viewModel;
    private readonly Func<CancellationToken, Task<ServiceDiagnosticsInfo?>> _diagnosticsLoader;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _diagnosticsStarted;

    public LogDetailsDialog(
        LogItemViewModel log,
        Func<CancellationToken, Task<ServiceDiagnosticsInfo?>> diagnosticsLoader)
    {
        _viewModel = new LogDetailsViewModel(log);
        _diagnosticsLoader = diagnosticsLoader;
        AvaloniaXamlLoader.Load(this);
        DataContext = _viewModel;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_diagnosticsStarted || !_viewModel.CanCollectDiagnostics)
        {
            return;
        }

        _diagnosticsStarted = true;
        _viewModel.IsDiagnosticsLoading = true;
        try
        {
            ServiceDiagnosticsInfo? diagnostics = await _diagnosticsLoader(_lifetimeCts.Token);
            if (diagnostics is null)
            {
                _viewModel.SetDiagnosticsError(
                    "This log does not identify a managed service, so deep service diagnostics are not available for it.");
                return;
            }

            _viewModel.ApplyDiagnostics(diagnostics);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _viewModel.SetDiagnosticsError(ex.Message);
        }
        finally
        {
            _viewModel.IsDiagnosticsLoading = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(_viewModel.BuildCopyText());
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
