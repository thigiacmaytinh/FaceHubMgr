using FaceHubManagement.Agent.Platform;

namespace FaceHubManagement.Agent.Firewall;

/// <summary>
/// Adds a narrow runtime netfilter override for ports disabled by FaceHub Management.
/// UFW user rules are evaluated after ufw-before-input, which accepts ESTABLISHED traffic,
/// and Docker published ports can bypass UFW's INPUT path entirely. These rules are inserted
/// ahead of those paths while leaving loopback traffic untouched.
/// </summary>
internal sealed class LinuxNetfilterPortOverride
{
    private const string CommentPrefix = "FaceHub Management TCP ";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);

    private readonly string? _iptablesPath = ResolveCommand("iptables");
    private readonly string? _ip6tablesPath = ResolveCommand("ip6tables");

    public bool IsAvailable => _iptablesPath is not null;

    public async Task<LinuxPortEnforcementResult> EnsureBlockedAsync(int port, CancellationToken cancellationToken)
    {
        if (_iptablesPath is null)
        {
            throw new InvalidOperationException(
                "iptables is not available. UFW can store the rule, but FaceHub cannot enforce the immediate host/Docker override.");
        }

        string comment = CommentPrefix + port;
        await EnsureInputRuleAsync(_iptablesPath, port, comment, cancellationToken).ConfigureAwait(false);

        bool ipv6Enforced = false;
        if (_ip6tablesPath is not null && IsIpv6Enabled())
        {
            await EnsureInputRuleAsync(_ip6tablesPath, port, comment, cancellationToken).ConfigureAwait(false);
            ipv6Enforced = true;
        }

        bool dockerEnforced = false;
        if (await ChainExistsAsync(_iptablesPath, "DOCKER-USER", cancellationToken).ConfigureAwait(false))
        {
            await EnsureDockerRuleAsync(_iptablesPath, port, comment, cancellationToken).ConfigureAwait(false);
            dockerEnforced = true;
        }

        return new LinuxPortEnforcementResult(true, ipv6Enforced, dockerEnforced);
    }

    public async Task RemoveBlockedAsync(int port, CancellationToken cancellationToken)
    {
        string comment = CommentPrefix + port;
        if (_iptablesPath is not null)
        {
            await RemoveAllInputRulesAsync(_iptablesPath, port, comment, cancellationToken).ConfigureAwait(false);
            if (await ChainExistsAsync(_iptablesPath, "DOCKER-USER", cancellationToken).ConfigureAwait(false))
            {
                await RemoveAllDockerRulesAsync(_iptablesPath, port, comment, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_ip6tablesPath is not null)
        {
            await RemoveAllInputRulesAsync(_ip6tablesPath, port, comment, cancellationToken).ConfigureAwait(false);
        }

        if (await IsBlockedAsync(port, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The FaceHub runtime block for TCP {port} is still present after cleanup. " +
                "The port remains blocked until that kernel rule is removed.");
        }
    }

    public async Task<bool> IsBlockedAsync(int port, CancellationToken cancellationToken)
    {
        IReadOnlySet<int> blocked = await GetBlockedPortsAsync([port], cancellationToken).ConfigureAwait(false);
        return blocked.Contains(port);
    }

    /// <summary>
    /// Returns the ports that still have a FaceHub runtime block in any path we manage.
    /// This is the effective runtime state and is intentionally checked independently from
    /// UFW's persisted rule list. A stale INPUT, IPv6, or DOCKER-USER override must keep the
    /// UI in Blocked state until it has actually been removed.
    /// </summary>
    public async Task<IReadOnlySet<int>> GetBlockedPortsAsync(
        IEnumerable<int> ports,
        CancellationToken cancellationToken)
    {
        HashSet<int> tracked = ports.Where(port => port is >= 1 and <= 65535).ToHashSet();
        var blocked = new HashSet<int>();
        if (tracked.Count == 0)
        {
            return blocked;
        }

        if (_iptablesPath is not null)
        {
            await CollectManagedPortsFromChainAsync(
                _iptablesPath, "INPUT", tracked, blocked, cancellationToken).ConfigureAwait(false);
            await CollectManagedPortsFromChainAsync(
                _iptablesPath, "DOCKER-USER", tracked, blocked, cancellationToken, ignoreMissingChain: true)
                .ConfigureAwait(false);
        }

        if (_ip6tablesPath is not null && IsIpv6Enabled())
        {
            await CollectManagedPortsFromChainAsync(
                _ip6tablesPath, "INPUT", tracked, blocked, cancellationToken).ConfigureAwait(false);
        }

        return blocked;
    }

    private static async Task CollectManagedPortsFromChainAsync(
        string command,
        string chain,
        IReadOnlySet<int> tracked,
        HashSet<int> blocked,
        CancellationToken cancellationToken,
        bool ignoreMissingChain = false)
    {
        ProcessResult result = await RunAsync(command, ["-w", "5", "-S", chain], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (ignoreMissingChain)
            {
                return;
            }

            EnsureSuccess(command, result);
        }

        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int marker = line.IndexOf(CommentPrefix, StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            int start = marker + CommentPrefix.Length;
            int end = start;
            while (end < line.Length && char.IsDigit(line[end]))
            {
                end++;
            }

            if (end > start && int.TryParse(line[start..end], out int port) && tracked.Contains(port))
            {
                blocked.Add(port);
            }
        }
    }

    private static async Task EnsureInputRuleAsync(
        string command,
        int port,
        string comment,
        CancellationToken cancellationToken)
    {
        string[] check = BuildInputCheckArguments(port, comment);
        if (await RuleExistsAsync(command, check, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string[] insert = BuildInputInsertArguments(port, comment, reject: true);
        ProcessResult result = await RunAsync(command, insert, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // Some minimal kernels do not have the REJECT target loaded. DROP is still a
            // correct enforcement fallback, although clients will time out instead of reset.
            insert = BuildInputInsertArguments(port, comment, reject: false);
            result = await RunAsync(command, insert, cancellationToken).ConfigureAwait(false);
        }

        EnsureSuccess(command, result);
    }

    private static async Task EnsureDockerRuleAsync(
        string command,
        int port,
        string comment,
        CancellationToken cancellationToken)
    {
        string[] check = BuildDockerCheckArguments(port, comment);
        if (await RuleExistsAsync(command, check, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string[] insert = BuildDockerInsertArguments(port, comment, reject: true);
        ProcessResult result = await RunAsync(command, insert, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            insert = BuildDockerInsertArguments(port, comment, reject: false);
            result = await RunAsync(command, insert, cancellationToken).ConfigureAwait(false);
        }

        EnsureSuccess(command, result);
    }

    private static async Task RemoveAllInputRulesAsync(
        string command,
        int port,
        string comment,
        CancellationToken cancellationToken)
    {
        foreach (bool reject in new[] { true, false })
        {
            string[] check = BuildInputCheckArguments(port, comment, reject);
            string[] delete = BuildInputDeleteArguments(port, comment, reject);
            while (await RuleExistsAsync(command, check, cancellationToken).ConfigureAwait(false))
            {
                ProcessResult result = await RunAsync(command, delete, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(command, result);
            }
        }
    }

    private static async Task RemoveAllDockerRulesAsync(
        string command,
        int port,
        string comment,
        CancellationToken cancellationToken)
    {
        foreach (bool reject in new[] { true, false })
        {
            string[] check = BuildDockerCheckArguments(port, comment, reject);
            string[] delete = BuildDockerDeleteArguments(port, comment, reject);
            while (await RuleExistsAsync(command, check, cancellationToken).ConfigureAwait(false))
            {
                ProcessResult result = await RunAsync(command, delete, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(command, result);
            }
        }
    }

    private static async Task<bool> ChainExistsAsync(
        string command,
        string chain,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(command, ["-w", "5", "-n", "-L", chain], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private static async Task<bool> RuleExistsAsync(
        string command,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(command, arguments, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private static Task<ProcessResult> RunAsync(
        string command,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(command, arguments, CommandTimeout, cancellationToken);

    private static string[] BuildInputCheckArguments(int port, string comment, bool reject = true) =>
        ["-w", "5", "-C", "INPUT", "!", "-i", "lo", "-p", "tcp", "--dport", port.ToString(),
            "-m", "comment", "--comment", comment, "-j", reject ? "REJECT" : "DROP",
            .. RejectTail(reject)];

    private static string[] BuildInputInsertArguments(int port, string comment, bool reject) =>
        ["-w", "5", "-I", "INPUT", "1", "!", "-i", "lo", "-p", "tcp", "--dport", port.ToString(),
            "-m", "comment", "--comment", comment, "-j", reject ? "REJECT" : "DROP",
            .. RejectTail(reject)];

    private static string[] BuildInputDeleteArguments(int port, string comment, bool reject) =>
        ["-w", "5", "-D", "INPUT", "!", "-i", "lo", "-p", "tcp", "--dport", port.ToString(),
            "-m", "comment", "--comment", comment, "-j", reject ? "REJECT" : "DROP",
            .. RejectTail(reject)];

    private static string[] BuildDockerCheckArguments(int port, string comment, bool reject = true) =>
        ["-w", "5", "-C", "DOCKER-USER", "-p", "tcp", "-m", "conntrack", "--ctstate", "DNAT",
            "--ctorigdstport", port.ToString(), "-m", "comment", "--comment", comment,
            "-j", reject ? "REJECT" : "DROP", .. RejectTail(reject)];

    private static string[] BuildDockerInsertArguments(int port, string comment, bool reject) =>
        ["-w", "5", "-I", "DOCKER-USER", "1", "-p", "tcp", "-m", "conntrack", "--ctstate", "DNAT",
            "--ctorigdstport", port.ToString(), "-m", "comment", "--comment", comment,
            "-j", reject ? "REJECT" : "DROP", .. RejectTail(reject)];

    private static string[] BuildDockerDeleteArguments(int port, string comment, bool reject) =>
        ["-w", "5", "-D", "DOCKER-USER", "-p", "tcp", "-m", "conntrack", "--ctstate", "DNAT",
            "--ctorigdstport", port.ToString(), "-m", "comment", "--comment", comment,
            "-j", reject ? "REJECT" : "DROP", .. RejectTail(reject)];

    private static string[] RejectTail(bool reject) => reject ? ["--reject-with", "tcp-reset"] : [];

    private static void EnsureSuccess(string command, ProcessResult result)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        string detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"{Path.GetFileName(command)} exited with code {result.ExitCode}."
            : detail.Trim());
    }

    private static bool IsIpv6Enabled()
    {
        const string path = "/proc/sys/net/ipv6/conf/all/disable_ipv6";
        try
        {
            return !File.Exists(path) || File.ReadAllText(path).Trim() != "1";
        }
        catch
        {
            return true;
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
}

internal sealed record LinuxPortEnforcementResult(
    bool Ipv4InputEnforced,
    bool Ipv6InputEnforced,
    bool DockerPublishedPortsEnforced);
