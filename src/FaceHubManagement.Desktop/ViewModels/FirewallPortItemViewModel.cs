using Avalonia.Media;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Desktop.ViewModels;

public sealed class FirewallPortItemViewModel : ViewModelBase
{
    private readonly Func<FirewallPortItemViewModel, Task> _toggleAction;
    private FirewallPortAccess _access;
    private string? _statusMessage;
    private bool _hasManagedAllowRule;
    private bool _isBusy;

    public FirewallPortItemViewModel(
        FirewallPortInfo info,
        Func<FirewallPortItemViewModel, Task> toggleAction)
    {
        _toggleAction = toggleAction;
        Port = info.Port;
        Name = info.Name;
        Protocol = info.Protocol;
        _access = info.Access;
        _statusMessage = info.StatusMessage;
        _hasManagedAllowRule = info.HasManagedAllowRule;
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, CanToggle);
    }

    public int Port { get; }
    public string PortText => Port.ToString();
    public string Name { get; private set; }
    public string Protocol { get; private set; }
    public FirewallPortAccess Access => _access;
    public string? StatusMessage => _statusMessage;
    public bool HasManagedAllowRule => _hasManagedAllowRule;
    public AsyncRelayCommand ToggleCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanInteract));
                ToggleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StateText => _access switch
    {
        FirewallPortAccess.AllowedManaged => "Allowed",
        FirewallPortAccess.AllowedExternal => "Allowed (system)",
        FirewallPortAccess.Blocked => "Blocked",
        FirewallPortAccess.NotAllowed => "Not Allowed",
        _ => "Unavailable"
    };

    // Firewall status is security-oriented: an open inbound port is a warning, while a denied port is safe.
    public IBrush StatusBrush => _access switch
    {
        FirewallPortAccess.AllowedManaged or FirewallPortAccess.AllowedExternal
            => new SolidColorBrush(Color.Parse("#D99A1D")),
        FirewallPortAccess.Blocked or FirewallPortAccess.NotAllowed
            => new SolidColorBrush(Color.Parse("#16B364")),
        _ => new SolidColorBrush(Color.Parse("#7A879B"))
    };

    public IBrush StatusBackgroundBrush => _access switch
    {
        FirewallPortAccess.AllowedManaged or FirewallPortAccess.AllowedExternal
            => new SolidColorBrush(Color.Parse("#FFF4D9")),
        FirewallPortAccess.Blocked or FirewallPortAccess.NotAllowed
            => new SolidColorBrush(Color.Parse("#E8F8F0")),
        _ => new SolidColorBrush(Color.Parse("#EEF1F5"))
    };

    public bool ShowEnable => _access is FirewallPortAccess.NotAllowed or FirewallPortAccess.Blocked;
    public bool ShowDisable => _access is FirewallPortAccess.AllowedManaged or FirewallPortAccess.AllowedExternal;
    public bool ShowUnavailable => _access == FirewallPortAccess.Unknown;
    public bool CanInteract => !_isBusy && _access != FirewallPortAccess.Unknown;

    public void Update(FirewallPortInfo info)
    {
        Name = info.Name;
        Protocol = info.Protocol;
        _access = info.Access;
        _statusMessage = info.StatusMessage;
        _hasManagedAllowRule = info.HasManagedAllowRule;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(Access));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasManagedAllowRule));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusBackgroundBrush));
        OnPropertyChanged(nameof(ShowEnable));
        OnPropertyChanged(nameof(ShowDisable));
        OnPropertyChanged(nameof(ShowUnavailable));
        OnPropertyChanged(nameof(CanInteract));
        ToggleCommand.RaiseCanExecuteChanged();
    }

    private bool CanToggle() => CanInteract;

    private Task ToggleAsync() => _toggleAction(this);
}
