using Avalonia.Media;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Desktop.ViewModels;

public sealed class ServiceItemViewModel : ViewModelBase
{
    private readonly Func<ServiceItemViewModel, string, Task> _action;
    private ServiceState _state;
    private int? _processId;
    private long? _uptimeSeconds;
    private string? _detail;
    private bool _isBusy;

    public ServiceItemViewModel(ManagedServiceInfo info, Func<ServiceItemViewModel, string, Task> action)
    {
        _action = action;
        Id = info.Id;
        Name = info.Name;
        Description = info.Description;
        Ports = info.Ports;
        StartCommand = new AsyncRelayCommand(() => _action(this, "start"), () => CanInteract);
        StopCommand = new AsyncRelayCommand(() => _action(this, "stop"), () => CanInteract);
        RestartCommand = new AsyncRelayCommand(() => _action(this, "restart"), () => CanInteract);
        Update(info);
    }

    public string Id { get; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public IReadOnlyList<int> Ports { get; private set; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }

    public ServiceState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                RaiseDerived();
            }
        }
    }

    public int? ProcessId
    {
        get => _processId;
        private set
        {
            if (SetProperty(ref _processId, value))
            {
                OnPropertyChanged(nameof(PidText));
            }
        }
    }

    public long? UptimeSeconds
    {
        get => _uptimeSeconds;
        private set
        {
            if (SetProperty(ref _uptimeSeconds, value))
            {
                OnPropertyChanged(nameof(UptimeText));
            }
        }
    }

    public string? Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
            }
        }
    }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseDerived();
            }
        }
    }

    public bool CanInteract => !IsBusy && State is not ServiceState.Starting and not ServiceState.Stopping;
    public bool ShowStart => State is ServiceState.Stopped or ServiceState.Failed or ServiceState.Unknown;
    public bool ShowRestart => State == ServiceState.Running;
    public bool ShowStop => State is ServiceState.Running or ServiceState.Starting;
    public string StateText => IsBusy ? "Working..." : State.ToString();
    public string PidText => ProcessId?.ToString() ?? "-";
    public string PortText => Ports.Count == 0 ? "-" : string.Join(", ", Ports);
    public string UptimeText => UptimeSeconds is null ? "-" : FormatDuration(UptimeSeconds.Value);
    public IBrush StatusBrush => State switch
    {
        ServiceState.Running => new SolidColorBrush(Color.Parse("#16B364")),
        ServiceState.Starting or ServiceState.Stopping => new SolidColorBrush(Color.Parse("#D99A1D")),
        ServiceState.Failed => new SolidColorBrush(Color.Parse("#E5484D")),
        _ => new SolidColorBrush(Color.Parse("#8A99B2"))
    };
    public IBrush StatusBackgroundBrush => State switch
    {
        ServiceState.Running => new SolidColorBrush(Color.Parse("#E8F8EF")),
        ServiceState.Starting or ServiceState.Stopping => new SolidColorBrush(Color.Parse("#FFF6DD")),
        ServiceState.Failed => new SolidColorBrush(Color.Parse("#FDEBEC")),
        _ => new SolidColorBrush(Color.Parse("#EEF2F7"))
    };
    public IBrush IconBackground => Id.Equals("nginx", StringComparison.OrdinalIgnoreCase)
        ? new SolidColorBrush(Color.Parse("#16A765"))
        : Id.Equals("mongodb", StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(Color.Parse("#47A248"))
            : new SolidColorBrush(Color.Parse("#1778F2"));
    public string IconPathData => Id.Equals("nginx", StringComparison.OrdinalIgnoreCase)
        ? "M 4 5 H 20 A 2 2 0 0 1 22 7 V 10 A 2 2 0 0 1 20 12 H 4 A 2 2 0 0 1 2 10 V 7 A 2 2 0 0 1 4 5 Z M 6 8 H 8 M 10 8 H 18 M 4 14 H 20 A 2 2 0 0 1 22 16 V 19 A 2 2 0 0 1 20 21 H 4 A 2 2 0 0 1 2 19 V 16 A 2 2 0 0 1 4 14 Z M 6 17 H 8 M 10 17 H 18"
        : Id.Equals("mongodb", StringComparison.OrdinalIgnoreCase)
            ? "M 4 6 C 4 3.8 7.6 2 12 2 C 16.4 2 20 3.8 20 6 C 20 8.2 16.4 10 12 10 C 7.6 10 4 8.2 4 6 Z M 4 10 C 5.6 12 8.5 13 12 13 C 15.5 13 18.4 12 20 10 V 14 C 20 16.2 16.4 18 12 18 C 7.6 18 4 16.2 4 14 Z M 4 18 C 5.6 20 8.5 21 12 21 C 15.5 21 18.4 20 20 18"
            : "M 12 4 A 3.5 3.5 0 1 1 12 11 A 3.5 3.5 0 1 1 12 4 Z M 5 19 C 5 15.8 8.1 13.6 12 13.6 C 15.9 13.6 19 15.8 19 19 V 20 H 5 Z M 3 9 V 6 H 6 M 18 6 H 21 V 9 M 3 15 V 18 H 6 M 18 18 H 21 V 15";

    public void Update(ManagedServiceInfo info)
    {
        Name = info.Name;
        Description = info.Description;
        Ports = info.Ports;
        State = info.State;
        ProcessId = info.ProcessId;
        UptimeSeconds = info.UptimeSeconds;
        Detail = info.Detail;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(PortText));
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(ShowStart));
        OnPropertyChanged(nameof(ShowRestart));
        OnPropertyChanged(nameof(ShowStop));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusBackgroundBrush));
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }

    private static string FormatDuration(long seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }
        return $"{span.Minutes}m {span.Seconds}s";
    }
}
