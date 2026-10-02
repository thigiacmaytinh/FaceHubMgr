using System.Text.RegularExpressions;
using FaceHubManagement.Agent.Platform;

namespace FaceHubManagement.Agent.Firewall;

/// <summary>
/// Handles only the Docker forwarding path that UFW does not reliably control.
/// Host traffic is managed exclusively through UFW. This class also removes
/// legacy FaceHub INPUT overrides created by older builds so there is never a
/// hidden host-level iptables rule behind UFW state.
/// </summary>
internal sealed partial class LinuxDockerFirewallOverride
{
    private const string LegacyCommentPrefix = "FaceHub Management TCP ";
    private const string DockerCommentPrefix = "FaceHub Management Docker TCP ";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);

    private readonly string? _iptablesPath = ResolveCommand("iptables");
    private readonly string? _ip6tablesPath = ResolveCommand("ip6tables");

    public bool IsAvailable => _iptablesPath is not null;

    public async Task<LinuxDockerFirewallState> GetStateAsync(
        IEnumerable<int> ports,
        CancellationToken cancellationToken)
    {
        HashSet<int> tracked = ports.Where(port => port is >= 1 and <= 65535).ToHashSet();
        var published = new HashSet<int>();
        var blocked = new HashSet<int>();
        if (tracked.Count == 0)
        {
            return new LinuxDockerFirewallState(published, blocked);
        }

        if (_iptablesPath is not null)
        {
            await CollectPublishedPortsAsync(_iptablesPath, tracked, published, cancellationToken)
                .ConfigureAwait(false);
            await CollectManagedDockerBlocksAsync(_iptablesPath, tracked, blocked, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_ip6tablesPath is not null && IsIpv6Enabled())
        {
            await CollectPublishedPortsAsync(_ip6tablesPath, tracked, published, cancellationToken)
                .ConfigureAwait(false);
            await CollectManagedDockerBlocksAsync(_ip6tablesPath, tracked, blocked, cancellationToken)
                .ConfigureAwait(false);
        }

        return new LinuxDockerFirewallState(published, blocked);
    }

    public async Task<bool> EnsureBlockedIfPublishedAsync(int port, CancellationToken cancellationToken)
    {
        await CleanupLegacyHostOverridesAsync(port, cancellationToken).ConfigureAwait(false);

        bool published = false;
        if (_iptablesPath is not null &&
            await IsDockerPublishedPortAsync(_iptablesPath, port, cancellationToken).ConfigureAwait(false))
        {
            await RemoveLegacyDockerRulesAsync(_iptablesPath, port, cancellationToken).ConfigureAwait(false);
            await EnsureDockerRuleAsync(_iptablesPath, port, cancellationToken).ConfigureAwait(false);
            published = true;
        }
        else if (_iptablesPath is not null)
        {
            await RemoveAllDockerRulesAsync(_iptablesPath, port, cancellationToken).ConfigureAwait(false);
        }

        if (_ip6tablesPath is not null && IsIpv6Enabled() &&
            await IsDockerPublishedPortAsync(_ip6tablesPath, port, cancellationToken).ConfigureAwait(false))
        {
            await RemoveLegacyDockerRulesAsync(_ip6tablesPath, port, cancellationToken).ConfigureAwait(false);
            await EnsureDockerRuleAsync(_ip6tablesPath, port, cancellationToken).ConfigureAwait(false);
            published = true;
        }
        else if (_ip6tablesPath is not null)
        {
            await RemoveAllDockerRulesAsync(_ip6tablesPath, port, cancellationToken).ConfigureAwait(false);
        }

        return published;
    }

    public async Task RemoveBlockedAsync(int port, CancellationToken cancellationToken)
    {
        await CleanupLegacyHostOverridesAsync(port, cancellationToken).ConfigureAwait(false);

        if (_iptablesPath is not null)
        {
            await RemoveAllDockerRulesAsync(_iptablesPath, port, cancellationToken).ConfigureAwait(false);
            await RemoveLegacyDockerRulesAsync(_iptablesPath, port, cancellationToken).ConfigureAwait(false);
        }

        if (_ip6tablesPath is not null)
        {
            await RemoveAllDockerRulesAsync(_ip6tablesPath, port, cancellationToken).ConfigureAwait(false);
            await RemoveLegacyDockerRulesAsync(_ip6tablesPath, port, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CleanupLegacyHostOverridesAsync(int port, CancellationToken cancellationToken)
    {
        if (_iptablesPath is not null)
        {
            await RemoveLegacyInputRulesAsync(_iptablesPath, port, cancellationToken).ConfigureAwait(false);
        }

        if (_ip6tablesPath is not null)
        {
            await RemoveLegacyInputRulesAsync(_ip6tablesPath, port, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CollectPublishedPortsAsync(
        string command,
        IReadOnlySet<int> tracked,
        HashSet<int> published,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(command, ["-w", "5", "-t", "nat", "-S", "DOCKER"], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return;
        }

        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = DestinationPortRegex().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int port) && tracked.Contains(port))
            {
                published.Add(port);
            }
        }
    }

    private static async Task CollectManagedDockerBlocksAsync(
        string command,
        IReadOnlySet<int> tracked,
        HashSet<int> blocked,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(command, ["-w", "5", "-S", "DOCKER-USER"], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return;
        }

        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int port = ExtractCommentPort(line, DockerCommentPrefix);
            if (port == 0)
            {
                port = ExtractCommentPort(line, LegacyCommentPrefix);
            }

            if (port != 0 && tracked.Contains(port))
            {
                blocked.Add(port);
            }
        }
    }

    private static async Task<bool> IsDockerPublishedPortAsync(
        string command,
        int port,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(command, ["-w", "5", "-t", "nat", "-S", "DOCKER"], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return false;
        }

        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = DestinationPortRegex().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int publishedPort) && publishedPort == port)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task EnsureDockerRuleAsync(
        string command,
        int port,
        CancellationToken cancellationToken)
    {
        string comment = DockerCommentPrefix + port;
        foreach (bool reject in new[] { true, false })
        {
            if (await RuleExistsAsync(command, BuildDockerCheckArguments(port, comment, reject), cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }
        }

        ProcessResult result = await RunAsync(
            command,
            BuildDockerInsertArguments(port, comment, reject: true),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            result = await RunAsync(
                command,
                BuildDockerInsertArguments(port, comment, reject: false),
                cancellationToken).ConfigureAwait(false);
        }

        EnsureSuccess(command, result);
    }

    private static async Task RemoveAllDockerRulesAsync(
        string command,
        int port,
        CancellationToken cancellationToken)
    {
        string comment = DockerCommentPrefix + port;
        foreach (bool reject in new[] { true, false })
        {
            string[] check = BuildDockerCheckArguments(port, comment, reject);
            string[] delete = BuildDockerDeleteArguments(port, comment, reject);
            while (await RuleExistsAsync(command, check, cancellationToken).ConfigureAwait(false))
            {
                EnsureSuccess(command, await RunAsync(command, delete, cancellationToken).ConfigureAwait(false));
            }
        }
    }

    private static async Task RemoveLegacyDockerRulesAsync(
        string command,
        int port,
        CancellationToken cancellationToken)
    {
        string comment = LegacyCommentPrefix + port;
        foreach (bool reject in new[] { true, false })
        {
            string[] check = BuildDockerCheckArguments(port, comment, reject);
            string[] delete = BuildDockerDeleteArguments(port, comment, reject);
            while (await RuleExistsAsync(command, check, cancellationToken).ConfigureAwait(false))
            {
                EnsureSuccess(command, await RunAsync(command, delete, cancellationToken).ConfigureAwait(false));
            }
        }
    }

    private static async Task RemoveLegacyInputRulesAsync(
        string command,
        int port,
        CancellationToken cancellationToken)
    {
        string comment = LegacyCommentPrefix + port;
        foreach (bool reject in new[] { true, false })
        {
            string[] check = BuildLegacyInputCheckArguments(port, comment, reject);
            string[] delete = BuildLegacyInputDeleteArguments(port, comment, reject);
            while (await RuleExistsAsync(command, check, cancellationToken).ConfigureAwait(false))
            {
                EnsureSuccess(command, await RunAsync(command, delete, cancellationToken).ConfigureAwait(false));
            }
        }
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

    private static string[] BuildDockerCheckArguments(int port, string comment, bool reject) =>
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

    private static string[] BuildLegacyInputCheckArguments(int port, string comment, bool reject) =>
        ["-w", "5", "-C", "INPUT", "!", "-i", "lo", "-p", "tcp", "--dport", port.ToString(),
            "-m", "comment", "--comment", comment, "-j", reject ? "REJECT" : "DROP",
            .. RejectTail(reject)];

    private static string[] BuildLegacyInputDeleteArguments(int port, string comment, bool reject) =>
        ["-w", "5", "-D", "INPUT", "!", "-i", "lo", "-p", "tcp", "--dport", port.ToString(),
            "-m", "comment", "--comment", comment, "-j", reject ? "REJECT" : "DROP",
            .. RejectTail(reject)];

    private static string[] RejectTail(bool reject) => reject ? ["--reject-with", "tcp-reset"] : [];

    private static int ExtractCommentPort(string line, string prefix)
    {
        int marker = line.IndexOf(prefix, StringComparison.Ordinal);
        if (marker < 0)
        {
            return 0;
        }

        int start = marker + prefix.Length;
        int end = start;
        while (end < line.Length && char.IsDigit(line[end]))
        {
            end++;
        }

        return end > start && int.TryParse(line[start..end], out int port) ? port : 0;
    }

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

    [GeneratedRegex(@"(?:^|\s)--dport\s+(\d+)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DestinationPortRegex();
}

internal sealed record LinuxDockerFirewallState(
    IReadOnlySet<int> PublishedPorts,
    IReadOnlySet<int> BlockedPorts);
