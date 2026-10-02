using Avalonia.Media;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Desktop.ViewModels;

public sealed class LogItemViewModel
{
    public LogItemViewModel(ServiceLogEntry entry)
    {
        Timestamp = entry.Timestamp;
        TimestampText = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Level = entry.Level;
        Component = entry.Component;
        Message = entry.Message;
        ServiceId = entry.ServiceId;
        Operation = entry.Operation;
        LevelBrush = entry.Level.ToUpperInvariant() switch
        {
            "ERROR" => new SolidColorBrush(Color.Parse("#E5484D")),
            "WARN" => new SolidColorBrush(Color.Parse("#D99A1D")),
            _ => new SolidColorBrush(Color.Parse("#1778F2"))
        };
    }

    public DateTimeOffset Timestamp { get; }
    public string TimestampText { get; }
    public string Level { get; }
    public string Component { get; }
    public string Message { get; }
    public string? ServiceId { get; }
    public string? Operation { get; }
    public IBrush LevelBrush { get; }
}
