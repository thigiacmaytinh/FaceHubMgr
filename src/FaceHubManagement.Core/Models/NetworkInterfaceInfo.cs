namespace FaceHubManagement.Core.Models;

public sealed class NetworkInterfaceInfo
{
    public int Index { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public string Subnet { get; set; } = string.Empty;
    public string PrimaryDns { get; set; } = string.Empty;
    public string SecondaryDns { get; set; } = string.Empty;
}
