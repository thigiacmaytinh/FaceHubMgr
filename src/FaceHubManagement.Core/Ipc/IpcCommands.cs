namespace FaceHubManagement.Core.Ipc;

public static class IpcCommands
{
    public const string Ping = "ping";
    public const string GetResources = "get_resources";
    public const string GetServices = "get_services";
    public const string GetPorts = "get_ports";
    public const string GetFirewallStatus = "get_firewall_status";
    public const string GetFirewallPorts = "get_firewall_ports";
    public const string EnableFirewall = "enable_firewall";
    public const string DisableFirewall = "disable_firewall";
    public const string AddFirewallPort = "add_firewall_port";
    public const string AllowFirewallPort = "allow_firewall_port";
    public const string DenyFirewallPort = "deny_firewall_port";
    public const string GetSystemInfo = "get_system_info";
    public const string GetLogs = "get_logs";
    public const string GetServiceDiagnostics = "get_service_diagnostics";
    public const string StartService = "start_service";
    public const string StopService = "stop_service";
    public const string RestartService = "restart_service";
}
