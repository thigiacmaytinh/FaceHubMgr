using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Desktop.ViewModels;

public sealed class LogDetailsViewModel : ViewModelBase
{
    private bool _isDiagnosticsLoading;
    private bool _hasDiagnostics;
    private string _diagnosticsError = string.Empty;
    private string _diagnosticsGeneratedAt = string.Empty;
    private string _diagnosticsServiceName = string.Empty;
    private string _diagnosticsOperation = string.Empty;
    private string _likelyCause = string.Empty;
    private string _serviceState = string.Empty;
    private string _serviceStatus = string.Empty;
    private string _exitCode = string.Empty;
    private string _configurationTest = string.Empty;
    private string _portConflicts = string.Empty;
    private string _recentServiceLogs = string.Empty;
    private string _applicationLogs = string.Empty;

    public LogDetailsViewModel(LogItemViewModel log)
    {
        Log = log;
        CanCollectDiagnostics = !string.IsNullOrWhiteSpace(log.ServiceId) ||
                                log.Component.Equals("Service", StringComparison.OrdinalIgnoreCase);
    }

    public LogItemViewModel Log { get; }
    public string TimestampText => Log.TimestampText;
    public string Level => Log.Level;
    public string Component => Log.Component;
    public string Message => Log.Message;
    public Avalonia.Media.IBrush LevelBrush => Log.LevelBrush;
    public bool CanCollectDiagnostics { get; }

    public bool IsDiagnosticsLoading
    {
        get => _isDiagnosticsLoading;
        set => SetProperty(ref _isDiagnosticsLoading, value);
    }

    public bool HasDiagnostics
    {
        get => _hasDiagnostics;
        private set => SetProperty(ref _hasDiagnostics, value);
    }

    public string DiagnosticsError
    {
        get => _diagnosticsError;
        private set
        {
            if (SetProperty(ref _diagnosticsError, value))
            {
                OnPropertyChanged(nameof(HasDiagnosticsError));
            }
        }
    }

    public bool HasDiagnosticsError => !string.IsNullOrWhiteSpace(DiagnosticsError);

    public string DiagnosticsGeneratedAt { get => _diagnosticsGeneratedAt; private set => SetProperty(ref _diagnosticsGeneratedAt, value); }
    public string DiagnosticsServiceName { get => _diagnosticsServiceName; private set => SetProperty(ref _diagnosticsServiceName, value); }
    public string DiagnosticsOperation { get => _diagnosticsOperation; private set => SetProperty(ref _diagnosticsOperation, value); }
    public string LikelyCause { get => _likelyCause; private set => SetProperty(ref _likelyCause, value); }
    public string ServiceState { get => _serviceState; private set => SetProperty(ref _serviceState, value); }
    public string ServiceStatus { get => _serviceStatus; private set { if (SetProperty(ref _serviceStatus, value)) OnPropertyChanged(nameof(HasServiceStatus)); } }
    public string ExitCode { get => _exitCode; private set { if (SetProperty(ref _exitCode, value)) OnPropertyChanged(nameof(HasExitCode)); } }
    public string ConfigurationTest { get => _configurationTest; private set { if (SetProperty(ref _configurationTest, value)) OnPropertyChanged(nameof(HasConfigurationTest)); } }
    public string PortConflicts { get => _portConflicts; private set { if (SetProperty(ref _portConflicts, value)) OnPropertyChanged(nameof(HasPortConflicts)); } }
    public string RecentServiceLogs { get => _recentServiceLogs; private set { if (SetProperty(ref _recentServiceLogs, value)) OnPropertyChanged(nameof(HasRecentServiceLogs)); } }
    public string ApplicationLogs { get => _applicationLogs; private set { if (SetProperty(ref _applicationLogs, value)) OnPropertyChanged(nameof(HasApplicationLogs)); } }

    public bool HasServiceStatus => !string.IsNullOrWhiteSpace(ServiceStatus);
    public bool HasExitCode => !string.IsNullOrWhiteSpace(ExitCode);
    public bool HasConfigurationTest => !string.IsNullOrWhiteSpace(ConfigurationTest);
    public bool HasPortConflicts => !string.IsNullOrWhiteSpace(PortConflicts);
    public bool HasRecentServiceLogs => !string.IsNullOrWhiteSpace(RecentServiceLogs);
    public bool HasApplicationLogs => !string.IsNullOrWhiteSpace(ApplicationLogs);

    public void ApplyDiagnostics(ServiceDiagnosticsInfo diagnostics)
    {
        DiagnosticsGeneratedAt = diagnostics.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        DiagnosticsServiceName = diagnostics.ServiceName;
        DiagnosticsOperation = string.IsNullOrWhiteSpace(diagnostics.Operation) ? "Current state" : diagnostics.Operation;
        LikelyCause = diagnostics.LikelyCause;
        ServiceState = diagnostics.State;
        ServiceStatus = diagnostics.ServiceStatus;
        ExitCode = diagnostics.ExitCode;
        ConfigurationTest = diagnostics.ConfigurationTest;
        PortConflicts = diagnostics.PortConflicts;
        RecentServiceLogs = diagnostics.RecentServiceLogs;
        ApplicationLogs = diagnostics.ApplicationLogs;
        DiagnosticsError = string.Empty;
        HasDiagnostics = true;
    }

    public void SetDiagnosticsError(string message)
    {
        HasDiagnostics = false;
        DiagnosticsError = message;
    }

    public string BuildCopyText()
    {
        var sections = new List<string>
        {
            $"{TimestampText} [{Level}] [{Component}]",
            Message
        };

        if (HasDiagnostics)
        {
            sections.Add($"Service diagnostics — {DiagnosticsServiceName} ({DiagnosticsOperation})");
            sections.Add($"Generated: {DiagnosticsGeneratedAt}");
            sections.Add($"State: {ServiceState}");
            sections.Add($"Likely cause:{Environment.NewLine}{LikelyCause}");
            AddSection(sections, "Service status", ServiceStatus);
            AddSection(sections, "Exit information", ExitCode);
            AddSection(sections, "Port ownership", PortConflicts);
            AddSection(sections, "Configuration test", ConfigurationTest);
            AddSection(sections, "Application logs", ApplicationLogs);
            AddSection(sections, "Operating-system service logs", RecentServiceLogs);
        }
        else if (HasDiagnosticsError)
        {
            sections.Add($"Diagnostics unavailable: {DiagnosticsError}");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, sections.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static void AddSection(ICollection<string> sections, string title, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sections.Add($"{title}:{Environment.NewLine}{value}");
        }
    }
}
