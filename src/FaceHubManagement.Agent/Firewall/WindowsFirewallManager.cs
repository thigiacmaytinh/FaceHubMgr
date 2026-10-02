using System.Text.Json;
using FaceHubManagement.Agent.Configuration;
using FaceHubManagement.Agent.Platform;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Firewall;

public sealed class WindowsFirewallManager : IFirewallManager
{
    private const string RulePrefix = "FaceHubManagement-TCP-";
    private const string BlockRulePrefix = "FaceHubManagement-Block-TCP-";
    private readonly FirewallPortStore _store;

    public WindowsFirewallManager(FirewallPortStore store)
    {
        _store = store;
    }

    public async Task<FirewallStatusInfo> GetStatusAsync(CancellationToken cancellationToken)
    {
        // Read the effective firewall state from ActiveStore (the same resultant policy
        // Windows Security reflects), not just the local persistent store. Enabled is a
        // GpoBoolean/CIM value, so normalize it to a real JSON boolean in PowerShell
        // before deserializing it.
        const string script = "$ErrorActionPreference='Stop'; " +
                              "$p=@(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop | ForEach-Object { " +
                              "[pscustomobject]@{ Name=[string]$_.Name; Enabled=(([string]$_.Enabled) -eq 'True') } " +
                              "}); " +
                              "$p | ConvertTo-Json -Compress";
        try
        {
            ProcessResult result = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(result.StandardOutput) ? "[]" : result.StandardOutput);
            var profiles = new List<FirewallProfileInfo>();
            foreach (JsonElement element in EnumerateObjects(document.RootElement))
            {
                profiles.Add(new FirewallProfileInfo
                {
                    Name = ReadString(element, "Name") ?? "Profile",
                    IsEnabled = ReadBool(element, "Enabled")
                });
            }

            bool anyEnabled = profiles.Any(profile => profile.IsEnabled);
            bool allEnabled = profiles.Count > 0 && profiles.All(profile => profile.IsEnabled);
            return new FirewallStatusInfo
            {
                ProviderName = "Windows Defender Firewall",
                IsAvailable = profiles.Count > 0,
                IsEnabled = allEnabled,
                IsPartiallyEnabled = anyEnabled && !allEnabled,
                StatusMessage = profiles.Count == 0 ? "No Windows firewall profiles were returned." : null,
                Profiles = profiles
            };
        }
        catch (Exception ex)
        {
            return new FirewallStatusInfo
            {
                ProviderName = "Windows Defender Firewall",
                IsAvailable = false,
                StatusMessage = ex.Message
            };
        }
    }

    public async Task<IReadOnlyList<FirewallPortInfo>> GetPortsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MonitoredPortDefinition> tracked = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
        if (tracked.Count == 0)
        {
            return Array.Empty<FirewallPortInfo>();
        }

        try
        {
            // Query port filters first, then resolve only the few rules that can affect the
            // ports FaceHub actually tracks. The previous implementation enumerated every
            // enabled inbound rule and issued one Get-NetFirewallPortFilter CIM query per
            // rule. On real Windows installations that can take tens of seconds and make the
            // desktop time out even though the agent itself is healthy.
            string wantedPorts = string.Join(",", tracked.Select(item =>
                item.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            string script = "$ErrorActionPreference='Stop'; " +
                            $"$wanted=@({wantedPorts}); " +
                            "$out=@(); " +
                            "$filters=@(Get-NetFirewallPortFilter -Protocol TCP -PolicyStore ActiveStore -ErrorAction Stop); " +
                            "foreach($f in $filters) { " +
                            "$matched=@(); " +
                            "foreach($rawSpec in @($f.LocalPort)) { " +
                            "foreach($rawPart in ([string]$rawSpec -split ',')) { " +
                            "$part=$rawPart.Trim(); " +
                            "foreach($p in $wanted) { " +
                            "$hit=$false; " +
                            "if($part -eq 'Any' -or $part -eq '*') { $hit=$true } " +
                            "elseif($part -match '^\\d+$') { $hit=([int]$part -eq [int]$p) } " +
                            "elseif($part -match '^(\\d+)-(\\d+)$') { " +
                            "$a=[int]$Matches[1]; $b=[int]$Matches[2]; " +
                            "$lo=[Math]::Min($a,$b); $hi=[Math]::Max($a,$b); " +
                            "$hit=([int]$p -ge $lo -and [int]$p -le $hi) } " +
                            "if($hit -and $matched -notcontains [int]$p) { $matched += [int]$p } " +
                            "} } } " +
                            "if($matched.Count -eq 0) { continue } " +
                            "$rules=@($f | Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { " +
                            "([string]$_.Direction -eq 'Inbound') -and ([string]$_.Enabled -eq 'True') }); " +
                            "foreach($r in $rules) { foreach($p in $matched) { " +
                            "$out += [pscustomobject]@{ Name=[string]$r.Name; DisplayName=[string]$r.DisplayName; Action=[string]$r.Action; LocalPort=[string]$p } " +
                            "} } " +
                            "}; " +
                            "$out | ConvertTo-Json -Compress";

            ProcessResult result = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
            var rules = new List<RuleSnapshot>();
            string json = string.IsNullOrWhiteSpace(result.StandardOutput) ? "[]" : result.StandardOutput;
            using JsonDocument document = JsonDocument.Parse(json);
            foreach (JsonElement element in EnumerateObjects(document.RootElement))
            {
                string localPort = ReadString(element, "LocalPort") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(localPort))
                {
                    continue;
                }

                rules.Add(new RuleSnapshot(
                    localPort,
                    ReadString(element, "Name") ?? string.Empty,
                    ReadString(element, "DisplayName") ?? string.Empty,
                    ReadString(element, "Action") ?? string.Empty));
            }

            return tracked
                .Select(item => BuildPortInfo(item, rules.Where(rule => PortSpecMatches(rule.LocalPort, item.Port))))
                .ToArray();
        }
        catch (Exception ex)
        {
            return tracked.Select(item => new FirewallPortInfo
            {
                Port = item.Port,
                Name = item.Name,
                Protocol = "TCP",
                Access = FirewallPortAccess.Unknown,
                StatusMessage = ex.Message
            }).ToArray();
        }
    }

    public async Task<FirewallStatusInfo> EnableAsync(CancellationToken cancellationToken)
    {
        await RunPowerShellAsync(
            "$ErrorActionPreference='Stop'; Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True",
            cancellationToken).ConfigureAwait(false);
        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FirewallStatusInfo> DisableAsync(CancellationToken cancellationToken)
    {
        await RunPowerShellAsync(
            "$ErrorActionPreference='Stop'; Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled False",
            cancellationToken).ConfigureAwait(false);
        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FirewallPortInfo> AddPortAsync(int port, string? name, CancellationToken cancellationToken)
    {
        MonitoredPortDefinition added = await _store.AddAsync(port, name, cancellationToken).ConfigureAwait(false);
        return PendingPortInfo(added);
    }

    public async Task<FirewallPortInfo> AllowPortAsync(int port, CancellationToken cancellationToken)
    {
        MonitoredPortDefinition definition = await _store.GetRequiredAsync(port, cancellationToken).ConfigureAwait(false);
        string ruleName = RuleName(port);
        string blockRuleName = BlockRuleName(port);
        string displayName = $"FaceHub Management - TCP {port}";
        string script = "$ErrorActionPreference='Stop'; " +
                        $"Get-NetFirewallRule -Name '{blockRuleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop; " +
                        $"Get-NetFirewallRule -Name '{ruleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop; " +
                        $"New-NetFirewallRule -Name '{ruleName}' -DisplayName '{displayName}' -Group 'FaceHub Management' " +
                        $"-Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort {port} -Profile Any | Out-Null";
        await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return new FirewallPortInfo
        {
            Port = definition.Port,
            Name = definition.Name,
            Protocol = "TCP",
            Access = FirewallPortAccess.AllowedManaged,
            HasManagedAllowRule = true,
            StatusMessage = "Enabled by FaceHub Management. Refreshing effective firewall state..."
        };
    }

    public async Task<FirewallPortInfo> DenyPortAsync(int port, CancellationToken cancellationToken)
    {
        MonitoredPortDefinition definition = await _store.GetRequiredAsync(port, cancellationToken).ConfigureAwait(false);
        string ruleName = RuleName(port);
        string blockRuleName = BlockRuleName(port);
        string displayName = $"FaceHub Management - Block TCP {port}";
        string script = "$ErrorActionPreference='Stop'; " +
                        $"Get-NetFirewallRule -Name '{ruleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop; " +
                        $"Get-NetFirewallRule -Name '{blockRuleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop; " +
                        $"New-NetFirewallRule -Name '{blockRuleName}' -DisplayName '{displayName}' -Group 'FaceHub Management' " +
                        $"-Direction Inbound -Action Block -Enabled True -Protocol TCP -LocalPort {port} -Profile Any | Out-Null";
        await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return new FirewallPortInfo
        {
            Port = definition.Port,
            Name = definition.Name,
            Protocol = "TCP",
            Access = FirewallPortAccess.Blocked,
            HasManagedAllowRule = false,
            StatusMessage = "Disabled by a FaceHub Management block override. Existing system rules are preserved."
        };
    }


    private static FirewallPortInfo PendingPortInfo(MonitoredPortDefinition definition) => new()
    {
        Port = definition.Port,
        Name = definition.Name,
        Protocol = "TCP",
        Access = FirewallPortAccess.Unknown,
        HasManagedAllowRule = false,
        StatusMessage = "Checking firewall rules..."
    };

    private async Task<FirewallPortInfo> GetRequiredPortInfoAsync(int port, CancellationToken cancellationToken)
    {
        IReadOnlyList<FirewallPortInfo> ports = await GetPortsAsync(cancellationToken).ConfigureAwait(false);
        return ports.First(item => item.Port == port);
    }

    private static FirewallPortInfo BuildPortInfo(MonitoredPortDefinition definition, IEnumerable<RuleSnapshot> portRules)
    {
        RuleSnapshot[] rules = portRules.ToArray();
        bool managedBlock = rules.Any(rule =>
            rule.Action.Equals("Block", StringComparison.OrdinalIgnoreCase) &&
            rule.Name.Equals(BlockRuleName(definition.Port), StringComparison.OrdinalIgnoreCase));
        bool externalBlock = rules.Any(rule =>
            rule.Action.Equals("Block", StringComparison.OrdinalIgnoreCase) &&
            !rule.Name.Equals(BlockRuleName(definition.Port), StringComparison.OrdinalIgnoreCase));
        bool managedAllow = rules.Any(rule =>
            rule.Action.Equals("Allow", StringComparison.OrdinalIgnoreCase) &&
            rule.Name.Equals(RuleName(definition.Port), StringComparison.OrdinalIgnoreCase));
        bool externalAllow = rules.Any(rule =>
            rule.Action.Equals("Allow", StringComparison.OrdinalIgnoreCase) &&
            !rule.Name.Equals(RuleName(definition.Port), StringComparison.OrdinalIgnoreCase));

        // Windows Firewall block rules take precedence over allow rules. A FaceHub-managed
        // block therefore gives the user an effective "Disable" operation even when an
        // existing Windows/system allow rule also matches the port, without deleting that
        // system rule.
        FirewallPortAccess access = managedBlock || externalBlock
            ? FirewallPortAccess.Blocked
            : managedAllow
                ? FirewallPortAccess.AllowedManaged
                : externalAllow
                    ? FirewallPortAccess.AllowedExternal
                    : FirewallPortAccess.NotAllowed;

        string? message = managedBlock
            ? "Disabled by a FaceHub Management block override. Existing system rules are preserved."
            : externalBlock
                ? "Blocked by an existing Windows firewall rule outside FaceHub Management."
                : access == FirewallPortAccess.AllowedExternal
                    ? "Allowed by an existing Windows firewall rule outside FaceHub Management."
                    : null;

        return new FirewallPortInfo
        {
            Port = definition.Port,
            Name = definition.Name,
            Protocol = "TCP",
            Access = access,
            HasManagedAllowRule = managedAllow,
            StatusMessage = message
        };
    }

    private static string RuleName(int port) => RulePrefix + port;
    private static string BlockRuleName(int port) => BlockRulePrefix + port;

    private static async Task<ProcessResult> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"PowerShell exited with code {result.ExitCode}."
                : detail.Trim());
        }

        return result;
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in root.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    yield return element;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.ToString() : null;

    private static bool ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric))
        {
            return numeric != 0;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (bool.TryParse(text, out bool parsed))
            {
                return parsed;
            }

            return int.TryParse(text, out int numericText) && numericText != 0;
        }

        return false;
    }

    private static bool PortSpecMatches(string specification, int port)
    {
        foreach (string rawPart in specification.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = rawPart.Trim();
            if (part.Equals("Any", StringComparison.OrdinalIgnoreCase) || part == "*")
            {
                return true;
            }

            if (int.TryParse(part, out int exact) && exact == port)
            {
                return true;
            }

            int separator = part.IndexOf('-');
            if (separator > 0 && separator < part.Length - 1 &&
                int.TryParse(part[..separator], out int start) &&
                int.TryParse(part[(separator + 1)..], out int end) &&
                port >= Math.Min(start, end) && port <= Math.Max(start, end))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record RuleSnapshot(string LocalPort, string Name, string DisplayName, string Action);
}
