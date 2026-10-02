using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Resources;

internal static class NetworkInfoCollector
{
    public static List<NetworkInterfaceInfo> GetActiveIpv4Interfaces()
    {
        var rows = new List<NetworkInterfaceInfo>();

        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(item => item.OperationalStatus == OperationalStatus.Up)
                     .Where(item => item.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel))
        {
            try
            {
                IPInterfaceProperties properties = networkInterface.GetIPProperties();
                string gateway = FirstIpv4(properties.GatewayAddresses.Select(item => item.Address));
                List<string> dns = properties.DnsAddresses
                    .Where(IsUsableAddress)
                    .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .Select(address => address.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToList();

                foreach (UnicastIPAddressInformation address in properties.UnicastAddresses
                             .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                             .Where(item => IsUsableAddress(item.Address)))
                {
                    rows.Add(new NetworkInterfaceInfo
                    {
                        IpAddress = address.Address.ToString(),
                        Gateway = EmptyAsDash(gateway),
                        Subnet = EmptyAsDash(GetSubnetMask(address)),
                        PrimaryDns = dns.Count > 0 ? dns[0] : "-",
                        SecondaryDns = dns.Count > 1 ? dns[1] : "-"
                    });
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                // A single adapter can disappear or expose incomplete platform data
                // while the snapshot is collected. Skip it and keep the rest usable.
            }
        }

        rows = rows
            .OrderByDescending(row => row.Gateway != "-")
            .ThenBy(row => ParseIpv4(row.IpAddress))
            .ToList();

        for (int index = 0; index < rows.Count; index++)
        {
            rows[index].Index = index + 1;
        }

        return rows;
    }

    private static bool IsUsableAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address) &&
        !address.Equals(IPAddress.Any) &&
        !address.Equals(IPAddress.IPv6Any);

    private static string FirstIpv4(IEnumerable<IPAddress> addresses) =>
        addresses.FirstOrDefault(address =>
            address.AddressFamily == AddressFamily.InterNetwork && IsUsableAddress(address))?.ToString() ?? string.Empty;

    private static string GetSubnetMask(UnicastIPAddressInformation address)
    {
        int prefixLength = address.PrefixLength;
        if (prefixLength is < 0 or > 32)
        {
            return string.Empty;
        }

        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return $"{(mask >> 24) & 0xff}.{(mask >> 16) & 0xff}.{(mask >> 8) & 0xff}.{mask & 0xff}";
    }

    private static uint ParseIpv4(string value)
    {
        byte[] bytes = IPAddress.Parse(value).GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static string EmptyAsDash(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
}
