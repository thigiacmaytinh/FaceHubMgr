using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Threading;
using FaceHubManagement.Core.Ipc;
using FaceHubManagement.Core.Models;
using FaceHubManagement.Core.Utilities;
using FaceHubManagement.Desktop.Services;

namespace FaceHubManagement.Desktop.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FirewallRefreshInterval = TimeSpan.FromSeconds(15);
    private const int OfflineHeartbeatFailureThreshold = 2;

    private readonly SettingsStore _settingsStore;
    private readonly SemaphoreSlim _firewallRefreshGate = new(1, 1);
    private DesktopSettings _settings;
    private AgentClient _client;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _toastCts;
    private bool _offlineToastShown;
    private int _consecutiveHeartbeatFailures;
    private string _currentPage = "Dashboard";
    private bool _isAgentOnline;
    private string _connectionText = "Connecting...";
    private string _platformText = RuntimeInformation.OSDescription;
    private string _statusMessage = string.Empty;
    private string _statusIcon = "✓";
    private bool _hasStatusMessage;
    private IBrush _statusBackgroundBrush = new SolidColorBrush(Color.Parse("#166534"));
    private IBrush _statusBorderBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private double _cpuPercent;
    private double _ramPercent;
    private double _diskPercent;
    private double _cudaPercent;
    private double _vramPercent;
    private bool _cudaMetricAvailable;
    private bool _vramMetricAvailable;
    private string _cpuDetail = "-";
    private string _ramDetail = "-";
    private string _diskDetail = "-";
    private string _cudaDetail = "-";
    private string _vramDetail = "-";
    private string _firewallProviderName = "Firewall";
    private bool _isFirewallAvailable;
    private bool _isFirewallEnabled;
    private bool _isFirewallPartiallyEnabled;
    private bool _isFirewallBusy;
    private string _firewallStatusText = "Checking...";
    private string _firewallStatusDetail = "Waiting for the management agent.";
    private string _firewallProfilesText = "-";
    private string _machineName = "-";
    private string _operatingSystem = "-";
    private string _cpuName = "-";
    private string _architecture = "-";
    private string _framework = "-";
    private string _systemUptime = "-";
    private string _gpuName = "-";
    private string _driverVersion = "-";
    private string _cudaVersion = "-";
    private string _selectedTheme;
    private double _resourceRefreshMs;
    private double _serviceRefreshMs;
    private double _logRefreshMs;
    private string _windowsPipeName;
    private string _linuxSocketPath;

    public MainWindowViewModel(SettingsStore settingsStore, DesktopSettings settings)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _selectedTheme = settings.Theme;
        _resourceRefreshMs = settings.ResourceRefreshMilliseconds;
        _serviceRefreshMs = settings.ServiceRefreshMilliseconds;
        _logRefreshMs = settings.LogRefreshMilliseconds;
        _windowsPipeName = settings.WindowsPipeName;
        _linuxSocketPath = settings.LinuxSocketPath;
        _client = CreateClient();

        NavigateCommand = new RelayCommand(parameter => Navigate(Convert.ToString(parameter) ?? "Dashboard"));
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        RefreshNowCommand = new AsyncRelayCommand(RefreshEverythingAsync);
        ShowAllLogsCommand = new RelayCommand(_ => Navigate("Logs"));
        ClearStatusCommand = new RelayCommand(_ => ClearStatus());
    }

    public ObservableCollection<ServiceItemViewModel> Services { get; } = [];
    public ObservableCollection<NetworkInterfaceInfo> NetworkInterfaces { get; } = [];
    public ObservableCollection<FirewallPortItemViewModel> FirewallPorts { get; } = [];
    public ObservableCollection<LogItemViewModel> RecentLogs { get; } = [];
    public ObservableCollection<LogItemViewModel> AllLogs { get; } = [];
    public IReadOnlyList<string> ThemeOptions { get; } = ["System", "Light", "Dark"];

    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand RefreshNowCommand { get; }
    public RelayCommand ShowAllLogsCommand { get; }
    public RelayCommand ClearStatusCommand { get; }

    public bool IsDashboardVisible => CurrentPage == "Dashboard";
    public bool IsFirewallVisible => CurrentPage == "Firewall";
    public bool IsSystemInfoVisible => CurrentPage == "SystemInfo";
    public bool IsSettingsVisible => CurrentPage == "Settings";
    public bool IsLogsVisible => CurrentPage == "Logs";

    public string CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(IsDashboardVisible));
                OnPropertyChanged(nameof(IsFirewallVisible));
                OnPropertyChanged(nameof(IsSystemInfoVisible));
                OnPropertyChanged(nameof(IsSettingsVisible));
                OnPropertyChanged(nameof(IsLogsVisible));
            }
        }
    }

    public bool IsAgentOnline
    {
        get => _isAgentOnline;
        private set
        {
            if (SetProperty(ref _isAgentOnline, value))
            {
                OnPropertyChanged(nameof(ConnectionBrush));
            }
        }
    }

    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }
    public string PlatformText { get => _platformText; private set => SetProperty(ref _platformText, value); }
    public IBrush ConnectionBrush => IsAgentOnline
        ? new SolidColorBrush(Color.Parse("#24D76A"))
        : new SolidColorBrush(Color.Parse("#E5484D"));

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string StatusIcon { get => _statusIcon; private set => SetProperty(ref _statusIcon, value); }
    public bool HasStatusMessage { get => _hasStatusMessage; private set => SetProperty(ref _hasStatusMessage, value); }
    public IBrush StatusBackgroundBrush { get => _statusBackgroundBrush; private set => SetProperty(ref _statusBackgroundBrush, value); }
    public IBrush StatusBorderBrush { get => _statusBorderBrush; private set => SetProperty(ref _statusBorderBrush, value); }

    public double CpuPercent { get => _cpuPercent; private set { if (SetProperty(ref _cpuPercent, value)) OnPropertyChanged(nameof(CpuPercentText)); } }
    public double RamPercent { get => _ramPercent; private set { if (SetProperty(ref _ramPercent, value)) OnPropertyChanged(nameof(RamPercentText)); } }
    public double DiskPercent { get => _diskPercent; private set { if (SetProperty(ref _diskPercent, value)) OnPropertyChanged(nameof(DiskPercentText)); } }
    public double CudaPercent { get => _cudaPercent; private set { if (SetProperty(ref _cudaPercent, value)) OnPropertyChanged(nameof(CudaPercentText)); } }
    public double VramPercent { get => _vramPercent; private set { if (SetProperty(ref _vramPercent, value)) OnPropertyChanged(nameof(VramPercentText)); } }
    public bool CudaMetricAvailable { get => _cudaMetricAvailable; private set { if (SetProperty(ref _cudaMetricAvailable, value)) OnPropertyChanged(nameof(CudaPercentText)); } }
    public bool VramMetricAvailable { get => _vramMetricAvailable; private set { if (SetProperty(ref _vramMetricAvailable, value)) OnPropertyChanged(nameof(VramPercentText)); } }
    public string CpuPercentText => $"{CpuPercent:0}%";
    public string RamPercentText => $"{RamPercent:0}%";
    public string DiskPercentText => $"{DiskPercent:0}%";
    public string CudaPercentText => CudaMetricAvailable ? $"{CudaPercent:0}%" : "N/A";
    public string VramPercentText => VramMetricAvailable ? $"{VramPercent:0}%" : "N/A";
    public string CpuDetail { get => _cpuDetail; private set => SetProperty(ref _cpuDetail, value); }
    public string RamDetail { get => _ramDetail; private set => SetProperty(ref _ramDetail, value); }
    public string DiskDetail { get => _diskDetail; private set => SetProperty(ref _diskDetail, value); }
    public string CudaDetail { get => _cudaDetail; private set => SetProperty(ref _cudaDetail, value); }
    public string VramDetail { get => _vramDetail; private set => SetProperty(ref _vramDetail, value); }

    public string FirewallProviderName { get => _firewallProviderName; private set => SetProperty(ref _firewallProviderName, value); }
    public bool IsFirewallAvailable
    {
        get => _isFirewallAvailable;
        private set
        {
            if (SetProperty(ref _isFirewallAvailable, value))
            {
                OnPropertyChanged(nameof(ShowEnableFirewall));
                OnPropertyChanged(nameof(ShowDisableFirewall));
                OnPropertyChanged(nameof(CanManageFirewall));
                OnPropertyChanged(nameof(FirewallStatusBrush));
                OnPropertyChanged(nameof(FirewallStatusBackgroundBrush));
            }
        }
    }
    public bool IsFirewallEnabled
    {
        get => _isFirewallEnabled;
        private set
        {
            if (SetProperty(ref _isFirewallEnabled, value))
            {
                OnPropertyChanged(nameof(ShowEnableFirewall));
                OnPropertyChanged(nameof(ShowDisableFirewall));
                OnPropertyChanged(nameof(FirewallStatusBrush));
                OnPropertyChanged(nameof(FirewallStatusBackgroundBrush));
            }
        }
    }
    public bool IsFirewallPartiallyEnabled
    {
        get => _isFirewallPartiallyEnabled;
        private set
        {
            if (SetProperty(ref _isFirewallPartiallyEnabled, value))
            {
                OnPropertyChanged(nameof(ShowEnableFirewall));
                OnPropertyChanged(nameof(ShowDisableFirewall));
                OnPropertyChanged(nameof(FirewallStatusBrush));
                OnPropertyChanged(nameof(FirewallStatusBackgroundBrush));
            }
        }
    }
    public bool IsFirewallBusy
    {
        get => _isFirewallBusy;
        private set
        {
            if (SetProperty(ref _isFirewallBusy, value))
            {
                OnPropertyChanged(nameof(CanManageFirewall));
            }
        }
    }
    public string FirewallStatusText { get => _firewallStatusText; private set => SetProperty(ref _firewallStatusText, value); }
    public string FirewallStatusDetail { get => _firewallStatusDetail; private set => SetProperty(ref _firewallStatusDetail, value); }
    public string FirewallProfilesText { get => _firewallProfilesText; private set => SetProperty(ref _firewallProfilesText, value); }
    public bool ShowEnableFirewall => IsFirewallAvailable && !IsFirewallEnabled;
    public bool ShowDisableFirewall => IsFirewallAvailable && (IsFirewallEnabled || IsFirewallPartiallyEnabled);
    public bool CanManageFirewall => IsFirewallAvailable && !IsFirewallBusy;
    public IBrush FirewallStatusBrush => !IsFirewallAvailable
        ? new SolidColorBrush(Color.Parse("#D99A1D"))
        : IsFirewallEnabled
            ? new SolidColorBrush(Color.Parse("#16B364"))
            : IsFirewallPartiallyEnabled
                ? new SolidColorBrush(Color.Parse("#D99A1D"))
                : new SolidColorBrush(Color.Parse("#7A879B"));
    public IBrush FirewallStatusBackgroundBrush => !IsFirewallAvailable
        ? new SolidColorBrush(Color.Parse("#FFF4D9"))
        : IsFirewallEnabled
            ? new SolidColorBrush(Color.Parse("#E8F8F0"))
            : IsFirewallPartiallyEnabled
                ? new SolidColorBrush(Color.Parse("#FFF4D9"))
                : new SolidColorBrush(Color.Parse("#EEF1F5"));

    public string MachineName { get => _machineName; private set => SetProperty(ref _machineName, value); }
    public string OperatingSystem { get => _operatingSystem; private set => SetProperty(ref _operatingSystem, value); }
    public string CpuName { get => _cpuName; private set => SetProperty(ref _cpuName, value); }
    public string Architecture { get => _architecture; private set => SetProperty(ref _architecture, value); }
    public string Framework { get => _framework; private set => SetProperty(ref _framework, value); }
    public string SystemUptime { get => _systemUptime; private set => SetProperty(ref _systemUptime, value); }
    public string GpuName { get => _gpuName; private set => SetProperty(ref _gpuName, value); }
    public string DriverVersion { get => _driverVersion; private set => SetProperty(ref _driverVersion, value); }
    public string CudaVersion { get => _cudaVersion; private set => SetProperty(ref _cudaVersion, value); }

    public string SelectedTheme { get => _selectedTheme; set => SetProperty(ref _selectedTheme, value); }
    public double ResourceRefreshMs
    {
        get => _resourceRefreshMs;
        set { if (SetProperty(ref _resourceRefreshMs, value)) OnPropertyChanged(nameof(ResourceRefreshLabel)); }
    }
    public double ServiceRefreshMs
    {
        get => _serviceRefreshMs;
        set { if (SetProperty(ref _serviceRefreshMs, value)) OnPropertyChanged(nameof(ServiceRefreshLabel)); }
    }
    public double LogRefreshMs
    {
        get => _logRefreshMs;
        set { if (SetProperty(ref _logRefreshMs, value)) OnPropertyChanged(nameof(LogRefreshLabel)); }
    }
    public string ResourceRefreshLabel => $"{ResourceRefreshMs:0} ms";
    public string ServiceRefreshLabel => $"{ServiceRefreshMs:0} ms";
    public string LogRefreshLabel => $"{LogRefreshMs:0} ms";
    public string WindowsPipeName { get => _windowsPipeName; set => SetProperty(ref _windowsPipeName, value); }
    public string LinuxSocketPath { get => _linuxSocketPath; set => SetProperty(ref _linuxSocketPath, value); }

    public void Start()
    {
        if (_refreshCts is not null)
        {
            return;
        }

        _refreshCts = new CancellationTokenSource();
        CancellationToken token = _refreshCts.Token;

        // Connection health is intentionally independent from telemetry. A slow
        // resource/service/log/firewall query must never mark the whole agent offline.
        _ = HeartbeatLoopAsync(token);
        _ = RefreshLoopAsync(token);
    }

    public void Stop()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        ClearStatus();
    }

    private void Navigate(string page)
    {
        CurrentPage = page is "Dashboard" or "Firewall" or "SystemInfo" or "Settings" or "Logs"
            ? page
            : "Dashboard";

        if (CurrentPage == "Firewall")
        {
            CancellationToken token = _refreshCts?.Token ?? CancellationToken.None;
            _ = RefreshFirewallSafeAsync(token, showToastOnFailure: false);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckHeartbeatAsync(cancellationToken, showOfflineToast: true).ConfigureAwait(false);

            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> CheckHeartbeatAsync(CancellationToken cancellationToken, bool showOfflineToast)
    {
        try
        {
            await _client.PingAsync(cancellationToken).ConfigureAwait(false);
            _consecutiveHeartbeatFailures = 0;
            _offlineToastShown = false;
            await SetConnectedAsync(true).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            int failures = Interlocked.Increment(ref _consecutiveHeartbeatFailures);
            if (failures >= OfflineHeartbeatFailureThreshold)
            {
                await SetConnectedAsync(false).ConfigureAwait(false);
                if (showOfflineToast && !_offlineToastShown)
                {
                    _offlineToastShown = true;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        ShowStatus($"Management agent unavailable: {ex.Message}", ToastKind.Error));
                }
            }

            return false;
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        Task[] loops =
        [
            RunPeriodicAsync(RefreshResourcesAsync, () => TimeSpan.FromMilliseconds(_settings.ResourceRefreshMilliseconds), cancellationToken),
            RunPeriodicAsync(RefreshServicesAsync, () => TimeSpan.FromMilliseconds(_settings.ServiceRefreshMilliseconds), cancellationToken),
            RunPeriodicAsync(RefreshLogsAsync, () => TimeSpan.FromMilliseconds(_settings.LogRefreshMilliseconds), cancellationToken),
            RunPeriodicAsync(RefreshSystemInfoAsync, () => TimeSpan.FromSeconds(30), cancellationToken),
            RunFirewallLoopAsync(cancellationToken)
        ];

        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task RunPeriodicAsync(
        Func<CancellationToken, Task> refresh,
        Func<TimeSpan> interval,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await refresh(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Component-level failures are isolated. The heartbeat loop is the
                // only authority that changes Agent Online/Offline state.
            }

            try
            {
                TimeSpan delay = interval();
                if (delay < TimeSpan.FromMilliseconds(100))
                {
                    delay = TimeSpan.FromMilliseconds(100);
                }
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunFirewallLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (CurrentPage == "Firewall")
            {
                await RefreshFirewallSafeAsync(cancellationToken, showToastOnFailure: false).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(FirewallRefreshInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshResourcesAsync(CancellationToken cancellationToken)
    {
        ResourceSnapshot resources = await _client.GetResourcesAsync(cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyResources(resources));
    }

    private async Task RefreshServicesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ManagedServiceInfo> services = await _client.GetServicesAsync(cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyServices(services));
    }

    private async Task RefreshLogsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ServiceLogEntry> logs = await _client.GetLogsAsync(200, cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyLogs(logs));
    }

    private async Task RefreshSystemInfoAsync(CancellationToken cancellationToken)
    {
        SystemInfoSnapshot systemInfo = await _client.GetSystemInfoAsync(cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => ApplySystemInfo(systemInfo));
    }

    private async Task RefreshEverythingAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Check the actual heartbeat separately. Telemetry failures no longer change
        // online/offline state and no single endpoint can block the others from updating.
        bool heartbeatOk = await CheckHeartbeatAsync(cts.Token, showOfflineToast: false).ConfigureAwait(false);
        if (!heartbeatOk && !IsAgentOnline)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ShowStatus("Refresh failed: the management agent heartbeat did not respond.", ToastKind.Error));
            return;
        }

        Task<string?> resources = TryRefreshComponentAsync("Resources", RefreshResourcesAsync, cts.Token);
        Task<string?> services = TryRefreshComponentAsync("Services", RefreshServicesAsync, cts.Token);
        Task<string?> logs = TryRefreshComponentAsync("Logs", RefreshLogsAsync, cts.Token);
        Task<string?> system = TryRefreshComponentAsync("System info", RefreshSystemInfoAsync, cts.Token);

        string?[] failures = await Task.WhenAll(resources, services, logs, system).ConfigureAwait(false);
        List<string> failedComponents = failures.Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToList();

        bool firewallOk = true;
        if (CurrentPage == "Firewall")
        {
            firewallOk = await RefreshFirewallSafeAsync(cts.Token, showToastOnFailure: false).ConfigureAwait(false);
            if (!firewallOk)
            {
                failedComponents.Add("Firewall");
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (failedComponents.Count == 0)
            {
                ShowStatus(
                    CurrentPage == "Firewall" ? "Firewall refreshed successfully." : "Dashboard refreshed successfully.",
                    ToastKind.Success);
            }
            else
            {
                ShowStatus(
                    $"Refresh incomplete: {string.Join(", ", failedComponents)}. Agent heartbeat is still online.",
                    ToastKind.Info);
            }
        });
    }

    private static async Task<string?> TryRefreshComponentAsync(
        string componentName,
        Func<CancellationToken, Task> refresh,
        CancellationToken cancellationToken)
    {
        try
        {
            await refresh(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return componentName;
        }
        catch
        {
            return componentName;
        }
    }

    private async Task<bool> RefreshFirewallSafeAsync(
        CancellationToken cancellationToken,
        bool showToastOnFailure)
    {
        bool entered;
        try
        {
            entered = await _firewallRefreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (!entered)
        {
            return true;
        }

        try
        {
            // Refresh the firewall state first and apply it immediately. Port-rule inspection
            // is intentionally independent: a slow/failed GetFirewallPorts request must never
            // overwrite a successfully-read Enabled/Disabled firewall state with "Unavailable".
            bool statusOk = false;
            bool portsOk = false;
            Exception? statusError = null;
            Exception? portsError = null;

            try
            {
                FirewallStatusInfo status = await _client.GetFirewallStatusAsync(cancellationToken).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => ApplyFirewallStatus(status));
                statusOk = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                statusError = ex;
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ApplyFirewallStatus(new FirewallStatusInfo
                    {
                        ProviderName = global::System.OperatingSystem.IsWindows() ? "Windows Defender Firewall" : "UFW Firewall",
                        IsAvailable = false,
                        StatusMessage = $"Firewall status could not be refreshed: {ex.Message}"
                    }));
            }

            // Do this after GetFirewallStatus instead of concurrently. Windows NetSecurity is
            // CIM-backed; running both PowerShell queries at the same time can make rule
            // enumeration dramatically slower on machines with many firewall rules.
            try
            {
                IReadOnlyList<FirewallPortInfo> ports = await _client.GetFirewallPortsAsync(cancellationToken).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => ApplyFirewallPorts(ports));
                portsOk = ports.All(port => port.Access != FirewallPortAccess.Unknown);
                if (!portsOk)
                {
                    portsError = new InvalidOperationException(
                        ports.FirstOrDefault(port => port.Access == FirewallPortAccess.Unknown)?.StatusMessage
                        ?? "Firewall port rules could not be determined.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                portsError = new TimeoutException("Firewall port inspection was cancelled before it completed.");
            }
            catch (Exception ex)
            {
                portsError = ex;
            }

            if (showToastOnFailure && (!statusOk || !portsOk))
            {
                string detail = !statusOk
                    ? statusError?.Message ?? "Firewall status is unavailable."
                    : portsError?.Message ?? "Firewall port inspection is unavailable.";
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ShowStatus(
                        statusOk
                            ? $"Firewall status refreshed, but port inspection is unavailable: {detail}"
                            : $"Firewall refresh failed: {detail}",
                        ToastKind.Info));
            }

            return statusOk && portsOk;
        }
        finally
        {
            _firewallRefreshGate.Release();
        }
    }

    public async Task<ServiceDiagnosticsInfo?> GetServiceDiagnosticsForLogAsync(
        LogItemViewModel log,
        CancellationToken cancellationToken = default)
    {
        string? serviceId = ResolveServiceId(log);
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return null;
        }

        string? operation = ResolveServiceOperation(log);
        return await _client.GetServiceDiagnosticsAsync(serviceId, operation, cancellationToken)
            .ConfigureAwait(false);
    }

    private string? ResolveServiceId(LogItemViewModel log)
    {
        if (!string.IsNullOrWhiteSpace(log.ServiceId))
        {
            return log.ServiceId;
        }

        if (!log.Component.Equals("Service", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ServiceItemViewModel? match = Services.FirstOrDefault(service =>
            log.Message.Contains(service.Id, StringComparison.OrdinalIgnoreCase) ||
            log.Message.Contains(service.Name, StringComparison.OrdinalIgnoreCase));
        return match?.Id;
    }

    private static string? ResolveServiceOperation(LogItemViewModel log)
    {
        if (!string.IsNullOrWhiteSpace(log.Operation))
        {
            return log.Operation;
        }

        foreach (string operation in new[] { "restart", "start", "stop" })
        {
            if (log.Message.Contains(operation, StringComparison.OrdinalIgnoreCase))
            {
                return operation;
            }
        }
        return null;
    }

    private async Task ExecuteServiceActionAsync(ServiceItemViewModel service, string action)
    {
        service.IsBusy = true;
        try
        {
            ManagedServiceInfo updated = action switch
            {
                "start" => await _client.StartServiceAsync(service.Id).ConfigureAwait(false),
                "stop" => await _client.StopServiceAsync(service.Id).ConfigureAwait(false),
                "restart" => await _client.RestartServiceAsync(service.Id).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported service action '{action}'.")
            };

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                service.Update(updated);
                ShowStatus($"{service.Name}: {action} completed successfully.", ToastKind.Success);
            });

            IReadOnlyList<ServiceLogEntry> logs = await _client.GetLogsAsync(200).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyLogs(logs));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ShowStatus($"{service.Name}: {action} failed — {ex.Message}", ToastKind.Error));

            try
            {
                IReadOnlyList<ServiceLogEntry> logs = await _client.GetLogsAsync(200).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => ApplyLogs(logs));
            }
            catch
            {
                // The regular log refresh loop will retry. Do not hide the original service failure.
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => service.IsBusy = false);
        }
    }

    public async Task SetFirewallEnabledAsync(bool enabled)
    {
        if (IsFirewallBusy || !IsFirewallAvailable)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => IsFirewallBusy = true);
        try
        {
            FirewallStatusInfo status = enabled
                ? await _client.EnableFirewallAsync().ConfigureAwait(false)
                : await _client.DisableFirewallAsync().ConfigureAwait(false);
            IReadOnlyList<ServiceLogEntry> logs = await _client.GetLogsAsync(200).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyFirewallStatus(status);
                ApplyLogs(logs);
                ShowStatus(enabled ? "Firewall enabled successfully." : "Firewall disabled successfully.", ToastKind.Success);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ShowStatus($"Unable to {(enabled ? "enable" : "disable")} firewall: {ex.Message}", ToastKind.Error));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsFirewallBusy = false);
        }
    }

    public async Task AddFirewallPortAsync(int port, string? name)
    {
        try
        {
            FirewallPortInfo added = await _client.AddFirewallPortAsync(port, name).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpsertFirewallPort(added);
                ShowStatus($"Added {added.Port}/TCP. Checking firewall status...", ToastKind.Success);
            });

            _ = RefreshFirewallAfterMutationAsync();
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ShowStatus($"Unable to add firewall port: {ex.Message}", ToastKind.Error));
        }
    }

    private async Task RefreshFirewallAfterMutationAsync()
    {
        try
        {
            await RefreshFirewallSafeAsync(CancellationToken.None, showToastOnFailure: false).ConfigureAwait(false);
            IReadOnlyList<ServiceLogEntry> logs = await _client.GetLogsAsync(200).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyLogs(logs));
        }
        catch
        {
            // The port is already persisted and visible. The regular firewall refresh loop will retry status inspection.
        }
    }

    private async Task ExecuteFirewallPortActionAsync(FirewallPortItemViewModel item)
    {
        if (!item.CanInteract)
        {
            return;
        }

        bool allow = item.Access is FirewallPortAccess.NotAllowed or FirewallPortAccess.Blocked;
        item.IsBusy = true;
        await _firewallRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            FirewallPortInfo updated = allow
                ? await _client.AllowFirewallPortAsync(item.Port).ConfigureAwait(false)
                : await _client.DenyFirewallPortAsync(item.Port).ConfigureAwait(false);

            // Apply the mutation result immediately. A background refresh cannot race this
            // operation because the same firewall gate is held until the verification read
            // completes. This is especially important for system-rule ports on Linux.
            await Dispatcher.UIThread.InvokeAsync(() => item.Update(updated));

            IReadOnlyList<FirewallPortInfo> ports = await _client.GetFirewallPortsAsync().ConfigureAwait(false);
            IReadOnlyList<ServiceLogEntry> logs = await _client.GetLogsAsync(200).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyFirewallPorts(ports);
                ApplyLogs(logs);

                FirewallPortItemViewModel? effective = FirewallPorts.FirstOrDefault(p => p.Port == item.Port);
                FirewallPortAccess effectiveAccess = effective?.Access ?? updated.Access;
                bool desiredReached = allow
                    ? effectiveAccess is FirewallPortAccess.AllowedManaged or FirewallPortAccess.AllowedExternal
                    : effectiveAccess == FirewallPortAccess.Blocked;

                if (!desiredReached)
                {
                    string requested = allow ? "allowed" : "denied";
                    ShowStatus(
                        $"Port {item.Port}/TCP was not {requested}. Effective state is {effectiveAccess}.",
                        ToastKind.Error);
                }
                else
                {
                    string verb = allow ? "allowed" : "denied";
                    ShowStatus($"Port {item.Port}/TCP {verb}.", ToastKind.Success);
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ShowStatus($"Firewall port action failed: {ex.Message}", ToastKind.Error));
        }
        finally
        {
            _firewallRefreshGate.Release();
            await Dispatcher.UIThread.InvokeAsync(() => item.IsBusy = false);
        }
    }

    public async Task SetAllFirewallPortsAsync(bool enable)
    {
        FirewallPortItemViewModel[] targets = FirewallPorts
            .Where(item => item.Access != FirewallPortAccess.Unknown)
            .ToArray();

        if (targets.Length == 0)
        {
            ShowStatus("No available firewall ports to update.", ToastKind.Info);
            return;
        }

        foreach (FirewallPortItemViewModel item in targets)
        {
            item.IsBusy = true;
        }

        var failures = new List<string>();
        int changed = 0;
        await _firewallRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (FirewallPortItemViewModel item in targets)
            {
                bool alreadyDesired = enable
                    ? item.Access is FirewallPortAccess.AllowedManaged or FirewallPortAccess.AllowedExternal
                    : item.Access == FirewallPortAccess.Blocked;
                if (alreadyDesired)
                {
                    continue;
                }

                try
                {
                    if (enable)
                    {
                        await _client.AllowFirewallPortAsync(item.Port).ConfigureAwait(false);
                    }
                    else
                    {
                        await _client.DenyFirewallPortAsync(item.Port).ConfigureAwait(false);
                    }
                    changed++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{item.Port}/TCP: {ex.Message}");
                }
            }

            IReadOnlyList<FirewallPortInfo> ports = await _client.GetFirewallPortsAsync().ConfigureAwait(false);
            IReadOnlyList<ServiceLogEntry> logs = await _client.GetLogsAsync(200).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyFirewallPorts(ports);
                ApplyLogs(logs);

                foreach (FirewallPortItemViewModel target in targets)
                {
                    FirewallPortItemViewModel? effective = FirewallPorts.FirstOrDefault(p => p.Port == target.Port);
                    if (effective is null)
                    {
                        failures.Add($"{target.Port}/TCP: verification result is missing.");
                        continue;
                    }

                    bool desiredReached = enable
                        ? effective.Access is FirewallPortAccess.AllowedManaged or FirewallPortAccess.AllowedExternal
                        : effective.Access == FirewallPortAccess.Blocked;
                    if (!desiredReached)
                    {
                        failures.Add($"{target.Port}/TCP: effective state is {effective.Access}.");
                    }
                }

                if (failures.Count == 0)
                {
                    string action = enable ? "allowed" : "denied";
                    ShowStatus($"Firewall ports {action}. {changed} rule state(s) updated and verified.", ToastKind.Success);
                }
                else
                {
                    string action = enable ? "allow" : "deny";
                    ShowStatus($"Unable to {action} {failures.Count} port state(s). {failures[0]}", ToastKind.Error);
                }
            });
        }
        finally
        {
            _firewallRefreshGate.Release();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (FirewallPortItemViewModel item in targets)
                {
                    item.IsBusy = false;
                }
            });
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            _settings = new DesktopSettings
            {
                Theme = SelectedTheme,
                ResourceRefreshMilliseconds = (int)Math.Round(ResourceRefreshMs),
                ServiceRefreshMilliseconds = (int)Math.Round(ServiceRefreshMs),
                LogRefreshMilliseconds = (int)Math.Round(LogRefreshMs),
                WindowsPipeName = WindowsPipeName,
                LinuxSocketPath = LinuxSocketPath
            };

            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
            _client = CreateClient();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Theme selection is intentionally disabled; keep the service available for a future restore.
                // ThemeService.Apply(_settings.Theme);
                ShowStatus("Settings saved and applied.", ToastKind.Success);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ShowStatus($"Unable to save settings: {ex.Message}", ToastKind.Error));
        }
    }

    private AgentClient CreateClient() => new(new AgentClientOptions
    {
        WindowsPipeName = _settings.WindowsPipeName,
        LinuxSocketPath = _settings.LinuxSocketPath,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        HealthRequestTimeout = TimeSpan.FromSeconds(3),
        TelemetryRequestTimeout = TimeSpan.FromSeconds(6),
        RequestTimeout = TimeSpan.FromSeconds(15),
        ServiceActionTimeout = TimeSpan.FromSeconds(40),
        FirewallRequestTimeout = TimeSpan.FromSeconds(30)
    });

    private void ApplyResources(ResourceSnapshot snapshot)
    {
        CpuPercent = Math.Clamp(snapshot.CpuUsagePercent, 0, 100);
        CpuDetail = $"{snapshot.LogicalProcessorCount} logical processors";

        RamPercent = snapshot.MemoryTotalBytes == 0
            ? 0
            : Math.Clamp(100.0 * snapshot.MemoryUsedBytes / snapshot.MemoryTotalBytes, 0, 100);
        RamDetail = $"{ByteFormatter.Format(snapshot.MemoryUsedBytes)} / {ByteFormatter.Format(snapshot.MemoryTotalBytes)}";

        DiskPercent = snapshot.DiskTotalBytes == 0
            ? 0
            : Math.Clamp(100.0 * snapshot.DiskUsedBytes / snapshot.DiskTotalBytes, 0, 100);
        DiskDetail = snapshot.DiskTotalBytes == 0
            ? "Disk information unavailable"
            : $"{ByteFormatter.Format(snapshot.DiskUsedBytes)} / {ByteFormatter.Format(snapshot.DiskTotalBytes)}";

        CudaMetricAvailable = snapshot.CudaUsageAvailable;
        CudaPercent = snapshot.CudaUsageAvailable
            ? Math.Clamp(snapshot.CudaUsagePercent, 0, 100)
            : 0;
        CudaDetail = snapshot.CudaUsageAvailable && !string.IsNullOrWhiteSpace(snapshot.GpuName)
            ? string.IsNullOrWhiteSpace(snapshot.CudaUsageSource)
                ? snapshot.GpuName
                : $"{snapshot.GpuName} · {snapshot.CudaUsageSource}"
            : snapshot.CudaUsageStatusMessage ?? snapshot.GpuName ?? "GPU information unavailable";

        VramMetricAvailable = snapshot.VramAvailable;
        VramPercent = snapshot.VramAvailable && snapshot.VramTotalBytes > 0
            ? Math.Clamp(100.0 * snapshot.VramUsedBytes / snapshot.VramTotalBytes, 0, 100)
            : 0;
        VramDetail = snapshot.VramAvailable
            ? $"{ByteFormatter.Format(snapshot.VramUsedBytes)} / {ByteFormatter.Format(snapshot.VramTotalBytes)}"
            : snapshot.GpuStatusMessage ?? "NVML VRAM data unavailable";
    }

    private void ApplyServices(IReadOnlyList<ManagedServiceInfo> services)
    {
        foreach (ManagedServiceInfo info in services)
        {
            ServiceItemViewModel? existing = Services.FirstOrDefault(item =>
                item.Id.Equals(info.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Services.Add(new ServiceItemViewModel(info, ExecuteServiceActionAsync));
            }
            else
            {
                existing.Update(info);
            }
        }

        foreach (ServiceItemViewModel stale in Services
                     .Where(item => services.All(info => !info.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
                     .ToArray())
        {
            Services.Remove(stale);
        }
    }

    private void ApplyFirewallStatus(FirewallStatusInfo status)
    {
        FirewallProviderName = status.ProviderName;
        IsFirewallAvailable = status.IsAvailable;
        IsFirewallEnabled = status.IsEnabled;
        IsFirewallPartiallyEnabled = status.IsPartiallyEnabled;

        FirewallStatusText = !status.IsAvailable
            ? "Unavailable"
            : status.IsEnabled
                ? "Enabled"
                : status.IsPartiallyEnabled
                    ? "Partially enabled"
                    : "Disabled";

        FirewallStatusDetail = status.StatusMessage ?? (!status.IsAvailable
            ? "Firewall status could not be determined."
            : status.IsEnabled
                ? "Inbound traffic is protected by the host firewall."
                : status.IsPartiallyEnabled
                    ? "Some firewall profiles are enabled while others are disabled."
                    : "The host firewall is currently disabled.");

        FirewallProfilesText = status.Profiles.Count == 0
            ? "-"
            : string.Join("     ", status.Profiles.Select(profile => $"{profile.Name}: {(profile.IsEnabled ? "ON" : "OFF")}"));

        OnPropertyChanged(nameof(FirewallStatusBrush));
        OnPropertyChanged(nameof(FirewallStatusBackgroundBrush));
        OnPropertyChanged(nameof(ShowEnableFirewall));
        OnPropertyChanged(nameof(ShowDisableFirewall));
        OnPropertyChanged(nameof(CanManageFirewall));
    }

    private void UpsertFirewallPort(FirewallPortInfo info)
    {
        FirewallPortItemViewModel? existing = FirewallPorts.FirstOrDefault(item => item.Port == info.Port);
        if (existing is null)
        {
            FirewallPorts.Add(new FirewallPortItemViewModel(info, ExecuteFirewallPortActionAsync));
        }
        else
        {
            existing.Update(info);
        }

        FirewallPortItemViewModel[] ordered = FirewallPorts.OrderBy(item => item.Port).ToArray();
        for (int targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            int currentIndex = FirewallPorts.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex)
            {
                FirewallPorts.Move(currentIndex, targetIndex);
            }
        }
    }

    private void ApplyFirewallPorts(IReadOnlyList<FirewallPortInfo> ports)
    {
        foreach (FirewallPortInfo info in ports)
        {
            FirewallPortItemViewModel? existing = FirewallPorts.FirstOrDefault(item => item.Port == info.Port);
            if (existing is null)
            {
                FirewallPorts.Add(new FirewallPortItemViewModel(info, ExecuteFirewallPortActionAsync));
            }
            else
            {
                existing.Update(info);
            }
        }

        foreach (FirewallPortItemViewModel stale in FirewallPorts
                     .Where(item => ports.All(info => info.Port != item.Port))
                     .ToArray())
        {
            FirewallPorts.Remove(stale);
        }

        FirewallPortItemViewModel[] ordered = FirewallPorts.OrderBy(item => item.Port).ToArray();
        for (int targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            int currentIndex = FirewallPorts.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex)
            {
                FirewallPorts.Move(currentIndex, targetIndex);
            }
        }
    }

    private void ApplyLogs(IReadOnlyList<ServiceLogEntry> logs)
    {
        AllLogs.Clear();
        foreach (ServiceLogEntry entry in logs)
        {
            AllLogs.Add(new LogItemViewModel(entry));
        }

        RecentLogs.Clear();
        foreach (ServiceLogEntry entry in logs.Take(50))
        {
            RecentLogs.Add(new LogItemViewModel(entry));
        }
    }

    private void ApplySystemInfo(SystemInfoSnapshot info)
    {
        MachineName = info.MachineName;
        OperatingSystem = info.OperatingSystem;
        PlatformText = info.OperatingSystem;
        CpuName = $"{info.CpuName} ({info.LogicalProcessorCount} logical processors)";
        Architecture = $"OS {info.OsArchitecture} / Process {info.ProcessArchitecture}";
        Framework = info.FrameworkDescription;
        SystemUptime = FormatDuration(info.SystemUptimeSeconds);
        GpuName = info.GpuName ?? "NVIDIA GPU not detected";
        DriverVersion = info.NvidiaDriverVersion ?? "-";
        CudaVersion = info.CudaDriverVersion ?? "-";

        NetworkInterfaces.Clear();
        foreach (NetworkInterfaceInfo networkInterface in info.NetworkInterfaces)
        {
            NetworkInterfaces.Add(networkInterface);
        }
    }

    private async Task SetConnectedAsync(bool connected)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsAgentOnline = connected;
            ConnectionText = connected ? "System Online" : "Agent Offline";
        });
    }

    private void ShowStatus(string message, ToastKind kind)
    {
        StatusMessage = message;
        switch (kind)
        {
            case ToastKind.Error:
                StatusIcon = "!";
                StatusBackgroundBrush = new SolidColorBrush(Color.Parse("#991B1B"));
                StatusBorderBrush = new SolidColorBrush(Color.Parse("#EF4444"));
                break;
            case ToastKind.Info:
                StatusIcon = "i";
                StatusBackgroundBrush = new SolidColorBrush(Color.Parse("#1E40AF"));
                StatusBorderBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
                break;
            default:
                StatusIcon = "✓";
                StatusBackgroundBrush = new SolidColorBrush(Color.Parse("#166534"));
                StatusBorderBrush = new SolidColorBrush(Color.Parse("#22C55E"));
                break;
        }

        HasStatusMessage = true;
        _toastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _toastCts = cts;
        _ = AutoDismissStatusAsync(cts);
    }

    private void ClearStatus()
    {
        _toastCts?.Cancel();
        _toastCts = null;
        HasStatusMessage = false;
    }

    private async Task AutoDismissStatusAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cts.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_toastCts, cts))
                {
                    _toastCts = null;
                    HasStatusMessage = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private static string FormatDuration(long seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays} days, {span.Hours} hours, {span.Minutes} minutes";
        }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours} hours, {span.Minutes} minutes";
        }
        return $"{span.Minutes} minutes, {span.Seconds} seconds";
    }

    private enum ToastKind
    {
        Success,
        Error,
        Info
    }
}
