using System.Text.RegularExpressions;
using FaceHubManagement.Agent.Configuration;
using FaceHubManagement.Agent.Platform;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Firewall;

public sealed partial class LinuxUfwFirewallManager : IFirewallManager
{
    private const string CommentPrefix = "FaceHub Management TCP ";
    private readonly FirewallPortStore _store;
    private readonly string? _ufwPath;
    private readonly LinuxDockerFirewallOverride _docker = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public LinuxUfwFirewallManager(FirewallPortStore store)
    {
        _store = store;
        _ufwPath = ResolveUfwPath();
    }

    public async Task<FirewallStatusInfo> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_ufwPath is null)
        {
            return new FirewallStatusInfo
            {
                ProviderName = "UFW Firewall",
                IsAvailable = false,
                StatusMessage = "UFW is not installed on this system."
            };
        }

        try
        {
            ProcessResult result = await RunUfwAsync(["status"], cancellationToken).ConfigureAwait(false);
            string firstLine = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? string.Empty;
            bool active = firstLine.Equals("Status: active", StringComparison.OrdinalIgnoreCase);
            bool inactive = firstLine.Equals("Status: inactive", StringComparison.OrdinalIgnoreCase);
            return new FirewallStatusInfo
            {
                ProviderName = "UFW Firewall",
                IsAvailable = active || inactive,
                IsEnabled = active,
                IsPartiallyEnabled = false,
                StatusMessage = active || inactive
                    ? null
                    : string.IsNullOrWhiteSpace(firstLine) ? "UFW returned no status." : firstLine,
                Profiles =
                [
                    new FirewallProfileInfo { Name = "UFW", IsEnabled = active }
                ]
            };
        }
        catch (Exception ex)
        {
            return new FirewallStatusInfo
            {
                ProviderName = "UFW Firewall",
                IsAvailable = false,
                StatusMessage = ex.Message
            };
        }
    }

    public async Task<IReadOnlyList<FirewallPortInfo>> GetPortsAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetPortsCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<IReadOnlyList<FirewallPortInfo>> GetPortsCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MonitoredPortDefinition> tracked = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
        if (_ufwPath is null)
        {
            return tracked.Select(item => Unknown(item, "UFW is not installed on this system.")).ToArray();
        }

        try
        {
            IReadOnlyList<UfwRule> rules = await ReadRulesAsync(cancellationToken).ConfigureAwait(false);

            // The Firewall page is a system-firewall front-end, not only a FaceHub watch list.
            // Merge persisted/tracked ports with every numeric TCP port that already exists in
            // UFW so administrators can operate existing system rules without opening a shell.
            var definitions = tracked
                .ToDictionary(item => item.Port, CloneDefinition);

            foreach (int port in rules.Where(rule => rule.IsInbound).Select(rule => rule.Port).Distinct())
            {
                definitions.TryAdd(port, new MonitoredPortDefinition
                {
                    Port = port,
                    Name = $"TCP {port}"
                });
            }

            int[] visiblePorts = definitions.Keys.OrderBy(port => port).ToArray();
            LinuxDockerFirewallState dockerState = await _docker
                .GetStateAsync(visiblePorts, cancellationToken)
                .ConfigureAwait(false);

            return visiblePorts
                .Select(port => BuildPortInfo(
                    definitions[port],
                    rules.Where(rule => rule.Port == port && rule.IsInbound),
                    dockerState.PublishedPorts.Contains(port),
                    dockerState.BlockedPorts.Contains(port)))
                .ToArray();
        }
        catch (Exception ex)
        {
            return tracked.Select(item => Unknown(item, ex.Message)).ToArray();
        }
    }

    public async Task<FirewallStatusInfo> EnableAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            await RunUfwAsync(["--force", "enable"], cancellationToken).ConfigureAwait(false);
            await ReconcileManagedRuntimeOverridesCoreAsync(cancellationToken).ConfigureAwait(false);
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<FirewallStatusInfo> DisableAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            IReadOnlyList<MonitoredPortDefinition> tracked = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
            foreach (MonitoredPortDefinition item in tracked)
            {
                await _docker.RemoveBlockedAsync(item.Port, cancellationToken).ConfigureAwait(false);
            }

            await RunUfwAsync(["--force", "disable"], cancellationToken).ConfigureAwait(false);
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<FirewallPortInfo> AddPortAsync(int port, string? name, CancellationToken cancellationToken)
    {
        MonitoredPortDefinition added = await _store.AddAsync(port, name, cancellationToken).ConfigureAwait(false);
        return PendingPortInfo(added);
    }

    public async Task<FirewallPortInfo> AllowPortAsync(int port, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            IReadOnlyList<UfwRule> rules = await ReadRulesAsync(cancellationToken).ConfigureAwait(false);
            MonitoredPortDefinition definition = await ResolveAndTrackPortAsync(port, rules, cancellationToken)
                .ConfigureAwait(false);

            // The Firewall page intentionally mirrors the administrator's UFW workflow.
            // Remove only simple/global rules for this exact TCP port (plus legacy FaceHub
            // commented rules), then create one unambiguous system rule and reload UFW.
            // Source-IP/subnet-specific rules are left untouched.
            await DeleteSimpleGlobalAndLegacyRulesAsync(port, rules, cancellationToken).ConfigureAwait(false);
            await RunUfwAsync(["allow", $"{port}/tcp"], cancellationToken).ConfigureAwait(false);
            await RunUfwAsync(["reload"], cancellationToken).ConfigureAwait(false);

            // Docker forwarding is a separate path. Allow removes any FaceHub DOCKER-USER
            // block, while normal host traffic is controlled only by the UFW rule above.
            await _docker.RemoveBlockedAsync(port, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<UfwRule> updatedRules = await ReadRulesAsync(cancellationToken).ConfigureAwait(false);
            LinuxDockerFirewallState dockerState = await _docker.GetStateAsync([port], cancellationToken)
                .ConfigureAwait(false);
            if (dockerState.BlockedPorts.Contains(port))
            {
                throw new InvalidOperationException(
                    $"TCP {port} still has a FaceHub Docker forwarding block after Allow.");
            }

            FirewallPortInfo result = BuildPortInfo(
                definition,
                updatedRules.Where(rule => rule.Port == port && rule.IsInbound),
                dockerState.PublishedPorts.Contains(port),
                dockerBlocked: false);

            if (result.Access is not FirewallPortAccess.AllowedManaged and not FirewallPortAccess.AllowedExternal)
            {
                throw CreateVerificationException("Allow", port, result, updatedRules);
            }

            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<FirewallPortInfo> DenyPortAsync(int port, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            IReadOnlyList<UfwRule> rules = await ReadRulesAsync(cancellationToken).ConfigureAwait(false);
            MonitoredPortDefinition definition = await ResolveAndTrackPortAsync(port, rules, cancellationToken)
                .ConfigureAwait(false);

            // Same semantics as running these commands manually as root:
            //   ufw deny <port>/tcp
            //   ufw reload
            // We first remove simple/global rules for the exact port so an older ALLOW cannot
            // remain ahead of the new DENY in UFW's first-match rule order.
            await DeleteSimpleGlobalAndLegacyRulesAsync(port, rules, cancellationToken).ConfigureAwait(false);
            await RunUfwAsync(["deny", $"{port}/tcp"], cancellationToken).ConfigureAwait(false);
            await RunUfwAsync(["reload"], cancellationToken).ConfigureAwait(false);

            // Existing connections can survive a policy change. Closing them is best effort and
            // does not change the persistent firewall configuration.
            await TerminateExistingConnectionsBestEffortAsync(port, cancellationToken).ConfigureAwait(false);

            // Docker-published traffic can bypass the host INPUT path. Keep the Docker-specific
            // block only for ports that are actually published by Docker.
            bool dockerPublished = await _docker
                .EnsureBlockedIfPublishedAsync(port, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<UfwRule> updatedRules = await ReadRulesAsync(cancellationToken).ConfigureAwait(false);
            LinuxDockerFirewallState dockerState = await _docker.GetStateAsync([port], cancellationToken)
                .ConfigureAwait(false);
            bool dockerBlocked = dockerState.BlockedPorts.Contains(port);
            if (dockerPublished && !dockerBlocked)
            {
                throw new InvalidOperationException(
                    $"TCP {port} is Docker-published but its DOCKER-USER block could not be verified.");
            }

            FirewallPortInfo result = BuildPortInfo(
                definition,
                updatedRules.Where(rule => rule.Port == port && rule.IsInbound),
                dockerPublished,
                dockerBlocked);

            if (result.Access != FirewallPortAccess.Blocked)
            {
                throw CreateVerificationException("Deny", port, result, updatedRules);
            }

            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ReconcileManagedRuntimeOverridesAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileManagedRuntimeOverridesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ReconcileManagedRuntimeOverridesCoreAsync(CancellationToken cancellationToken)
    {
        if (_ufwPath is null)
        {
            return;
        }

        FirewallStatusInfo status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MonitoredPortDefinition> tracked = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
        bool migratedUfwRule = false;

        // Migrate legacy FaceHub-commented UFW overrides into ordinary system UFW rules while
        // preserving the effective state that existed before the upgrade. Administrator-created
        // source-IP/subnet-specific rules are never removed by this migration.
        foreach (MonitoredPortDefinition item in tracked)
        {
            IReadOnlyList<UfwRule> currentRules = await ReadRulesAsync(cancellationToken).ConfigureAwait(false);
            UfwRule? effectiveGlobal = currentRules
                .Where(rule => rule.Port == item.Port && rule.IsInbound && rule.IsGlobal)
                .OrderBy(rule => rule.Number)
                .FirstOrDefault();

            bool hasLegacyManagedRule = currentRules.Any(rule => rule.Port == item.Port && rule.IsInbound && rule.IsManaged);
            if (!hasLegacyManagedRule)
            {
                continue;
            }

            if (effectiveGlobal?.IsManaged == true)
            {
                string action = effectiveGlobal.Action == "ALLOW" ? "allow" : "deny";
                await DeleteSimpleGlobalAndLegacyRulesAsync(item.Port, currentRules, cancellationToken)
                    .ConfigureAwait(false);
                await RunUfwAsync([action, $"{item.Port}/tcp"], cancellationToken).ConfigureAwait(false);
                migratedUfwRule = true;
            }
            else
            {
                await DeleteLegacyManagedRulesForPortAsync(item.Port, currentRules, cancellationToken)
                    .ConfigureAwait(false);
                migratedUfwRule = true;
            }
        }

        if (migratedUfwRule)
        {
            await RunUfwAsync(["reload"], cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<UfwRule> rules = await ReadRulesAsync(cancellationToken).ConfigureAwait(false);
        foreach (MonitoredPortDefinition item in tracked)
        {
            await _docker.CleanupLegacyHostOverridesAsync(item.Port, cancellationToken).ConfigureAwait(false);

            UfwRule? effectiveGlobal = rules
                .Where(rule => rule.Port == item.Port && rule.IsInbound && rule.IsGlobal)
                .OrderBy(rule => rule.Number)
                .FirstOrDefault();

            if (status.IsEnabled && effectiveGlobal is not null && effectiveGlobal.Action is "DENY" or "REJECT")
            {
                await _docker.EnsureBlockedIfPublishedAsync(item.Port, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _docker.RemoveBlockedAsync(item.Port, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<MonitoredPortDefinition> ResolveAndTrackPortAsync(
        int port,
        IReadOnlyList<UfwRule> rules,
        CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        IReadOnlyList<MonitoredPortDefinition> tracked = await _store.GetAsync(cancellationToken)
            .ConfigureAwait(false);
        MonitoredPortDefinition? existing = tracked.FirstOrDefault(item => item.Port == port);
        if (existing is not null)
        {
            return CloneDefinition(existing);
        }

        // A port discovered directly from UFW is immediately manageable from the GUI. Persist
        // it the first time an action is performed so Docker reconciliation and later refreshes
        // continue to cover it even if the original system rule is subsequently changed.
        if (!rules.Any(rule => rule.Port == port))
        {
            throw new KeyNotFoundException($"TCP {port} is not present in the firewall list.");
        }

        return await _store.AddAsync(port, $"TCP {port}", cancellationToken).ConfigureAwait(false);
    }

    private static MonitoredPortDefinition CloneDefinition(MonitoredPortDefinition item) => new()
    {
        Port = item.Port,
        Name = string.IsNullOrWhiteSpace(item.Name) ? $"TCP {item.Port}" : item.Name.Trim()
    };

    private async Task DeleteSimpleGlobalAndLegacyRulesAsync(
        int port,
        IReadOnlyList<UfwRule> rules,
        CancellationToken cancellationToken)
    {
        int[] numbers = rules
            .Where(rule => rule.Port == port && rule.IsInbound && (rule.IsGlobal || rule.IsManaged))
            .Select(rule => rule.Number)
            .Distinct()
            .OrderByDescending(number => number)
            .ToArray();

        foreach (int number in numbers)
        {
            await RunUfwAsync(["--force", "delete", number.ToString()], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteLegacyManagedRulesForPortAsync(
        int port,
        IReadOnlyList<UfwRule> rules,
        CancellationToken cancellationToken)
    {
        int[] numbers = rules
            .Where(rule => rule.Port == port && rule.IsInbound && rule.IsManaged)
            .Select(rule => rule.Number)
            .Distinct()
            .OrderByDescending(number => number)
            .ToArray();

        foreach (int number in numbers)
        {
            await RunUfwAsync(["--force", "delete", number.ToString()], cancellationToken).ConfigureAwait(false);
        }
    }


    private static InvalidOperationException CreateVerificationException(
        string operation,
        int port,
        FirewallPortInfo result,
        IReadOnlyList<UfwRule> allRules)
    {
        string matching = string.Join(
            ", ",
            allRules
                .Where(rule => rule.Port == port && rule.IsInbound)
                .OrderBy(rule => rule.Number)
                .Select(rule => $"#{rule.Number}:{rule.Action}{(rule.IsManaged ? ":FaceHub" : ":system")}"));

        if (string.IsNullOrWhiteSpace(matching))
        {
            matching = "none";
        }

        return new InvalidOperationException(
            $"{operation} verification failed for TCP {port}: effective state is {result.Access}; " +
            $"matching UFW rules: {matching}. " +
            "No success was reported because the requested effective firewall state was not reached.");
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

    private async Task<IReadOnlyList<UfwRule>> ReadRulesAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await RunUfwAsync(["status", "numbered"], cancellationToken).ConfigureAwait(false);
        return ParseRules(result.StandardOutput);
    }

    private static IReadOnlyList<UfwRule> ParseRules(string output)
    {
        var rules = new List<UfwRule>();
        foreach (string line in output.Split('\n'))
        {
            Match match = NumberedRuleRegex().Match(line);
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, out int number) ||
                !int.TryParse(match.Groups[2].Value, out int port))
            {
                continue;
            }

            string action = match.Groups[3].Value.ToUpperInvariant();
            string direction = match.Groups[4].Value.ToUpperInvariant();
            bool isInbound = !direction.Equals("OUT", StringComparison.OrdinalIgnoreCase);
            bool managed = line.Contains(CommentPrefix + port, StringComparison.OrdinalIgnoreCase);
            bool isGlobal = GlobalSourceRegex().IsMatch(line);
            rules.Add(new UfwRule(number, port, action, managed, isGlobal, isInbound));
        }
        return rules;
    }

    private static FirewallPortInfo BuildPortInfo(
        MonitoredPortDefinition definition,
        IEnumerable<UfwRule> matching,
        bool dockerPublished,
        bool dockerBlocked)
    {
        UfwRule[] rules = matching.Where(rule => rule.IsInbound).OrderBy(rule => rule.Number).ToArray();
        UfwRule? effective = rules.FirstOrDefault(rule => rule.IsGlobal) ?? rules.FirstOrDefault();
        bool managedAllow = rules.Any(rule => rule.Action == "ALLOW" && rule.IsManaged);

        FirewallPortAccess access;
        string? message = null;

        // Docker forwarding is a separate path. A stale/active Docker block is always an
        // effective block even if UFW currently displays ALLOW.
        if (dockerBlocked)
        {
            access = FirewallPortAccess.Blocked;
            message = "Blocked for Docker-published traffic by FaceHub Management; host traffic is governed by UFW.";
        }
        else if (effective is null)
        {
            access = FirewallPortAccess.NotAllowed;
            if (dockerPublished)
            {
                message = "Docker publishes this port, but no matching UFW host rule is present.";
            }
        }
        else if (effective.Action is "DENY" or "REJECT")
        {
            if (dockerPublished && effective.IsManaged)
            {
                // UFW alone does not prove a Docker-published port is blocked.
                access = FirewallPortAccess.Unknown;
                message = "UFW denies host traffic, but the Docker forwarding block is missing. Disable the port again to reconcile it.";
            }
            else
            {
                access = FirewallPortAccess.Blocked;
                message = effective.IsManaged
                    ? "Blocked by a legacy FaceHub UFW rule."
                    : "Blocked by a UFW system rule.";
            }
        }
        else if (effective.Action == "ALLOW" && effective.IsManaged)
        {
            access = FirewallPortAccess.AllowedManaged;
            message = dockerPublished
                ? "Allowed by a legacy FaceHub UFW rule; no FaceHub DOCKER-USER block is active."
                : "Allowed by a legacy FaceHub UFW rule.";
        }
        else if (effective.Action == "ALLOW")
        {
            access = FirewallPortAccess.AllowedExternal;
            message = dockerPublished
                ? "Allowed by a UFW system rule. This port is also Docker-published."
                : "Allowed by a UFW system rule.";
        }
        else
        {
            access = FirewallPortAccess.NotAllowed;
        }

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

    private static FirewallPortInfo Unknown(MonitoredPortDefinition definition, string message) => new()
    {
        Port = definition.Port,
        Name = definition.Name,
        Protocol = "TCP",
        Access = FirewallPortAccess.Unknown,
        StatusMessage = message
    };

    private static async Task TerminateExistingConnectionsBestEffortAsync(
        int port,
        CancellationToken cancellationToken)
    {
        string? conntrack = ResolveCommand("conntrack");
        if (conntrack is null)
        {
            return;
        }

        try
        {
            // conntrack may return a non-zero status when there are no matching flows. That is
            // harmless here: the persistent firewall policy has already been changed by UFW.
            _ = await ProcessRunner.RunAsync(
                conntrack,
                ["-D", "-p", "tcp", "--dport", port.ToString()],
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best effort only. Never fail a UFW rule mutation because conntrack is unavailable.
        }
    }

    private static string? ResolveCommand(string name)
    {
        string[] candidates =
        [
            $"/usr/sbin/{name}",
            $"/usr/bin/{name}",
            $"/sbin/{name}",
            $"/bin/{name}"
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<ProcessResult> RunUfwAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        var envArguments = new List<string> { "LC_ALL=C", _ufwPath! };
        envArguments.AddRange(arguments);
        ProcessResult result = await ProcessRunner.RunAsync(
            "/usr/bin/env",
            envArguments,
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"ufw exited with code {result.ExitCode}."
                : detail.Trim());
        }

        return result;
    }

    private void EnsureAvailable()
    {
        if (_ufwPath is null)
        {
            throw new InvalidOperationException("UFW is not installed on this system.");
        }
    }

    private static string? ResolveUfwPath()
    {
        if (File.Exists("/usr/sbin/ufw")) return "/usr/sbin/ufw";
        if (File.Exists("/usr/bin/ufw")) return "/usr/bin/ufw";
        return null;
    }

    [GeneratedRegex(@"^\s*\[\s*(\d+)\]\s+(\d+)(?:/tcp)?(?:\s+\(v6\))?\s+(ALLOW|DENY|REJECT)(?:\s+(IN|OUT))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedRuleRegex();

    [GeneratedRegex(@"\sAnywhere(?:\s+\(v6\))?(?:\s+#.*)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GlobalSourceRegex();

    private sealed record UfwRule(int Number, int Port, string Action, bool IsManaged, bool IsGlobal, bool IsInbound);
}
