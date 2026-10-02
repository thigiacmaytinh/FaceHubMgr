using Avalonia.Media;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Desktop.ViewModels;

public sealed class PortStatusItemViewModel : ViewModelBase
{
    private bool _isAvailable = true;
    private bool _isListening;
    private IReadOnlyList<string> _endpoints = Array.Empty<string>();
    private string? _statusMessage;

    public PortStatusItemViewModel(PortStatusInfo info)
    {
        Port = info.Port;
        Name = info.Name;
        Protocol = info.Protocol;
        Update(info);
    }

    public int Port { get; }
    public string Name { get; private set; }
    public string Protocol { get; private set; }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                RaiseDerived();
            }
        }
    }

    public bool IsListening
    {
        get => _isListening;
        private set
        {
            if (SetProperty(ref _isListening, value))
            {
                RaiseDerived();
            }
        }
    }

    public IReadOnlyList<string> Endpoints
    {
        get => _endpoints;
        private set
        {
            _endpoints = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public string PortText => Port.ToString();
    public string StateText => !IsAvailable ? "Unavailable" : IsListening ? "Listening" : "Closed";
    public string DetailText => !IsAvailable
        ? StatusMessage ?? "Unable to inspect TCP listeners"
        : IsListening && Endpoints.Count > 0
            ? string.Join("  •  ", Endpoints)
            : "No TCP listener detected";

    public IBrush StatusBrush => !IsAvailable
        ? new SolidColorBrush(Color.Parse("#E5484D"))
        : IsListening
            ? new SolidColorBrush(Color.Parse("#16B364"))
            : new SolidColorBrush(Color.Parse("#8A99B2"));

    public IBrush StatusBackgroundBrush => !IsAvailable
        ? new SolidColorBrush(Color.Parse("#FDEBEC"))
        : IsListening
            ? new SolidColorBrush(Color.Parse("#E8F8EF"))
            : new SolidColorBrush(Color.Parse("#EEF2F7"));

    public void Update(PortStatusInfo info)
    {
        Name = info.Name;
        Protocol = info.Protocol;
        Endpoints = info.Endpoints.ToArray();
        StatusMessage = info.StatusMessage;
        IsAvailable = info.IsAvailable;
        IsListening = info.IsListening;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(DetailText));
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusBackgroundBrush));
    }
}
