using System.Diagnostics;
using System.Text;
using FaceHubManagement.Agent.Configuration;
using FaceHubManagement.Agent.Platform;
using FaceHubManagement.Core.Models;

namespace FaceHubManagement.Agent.Services;

public sealed class ServiceDiagnosticsCollector
{
    private static readonly TimeSpan DiagnosticCommandTimeout = TimeSpan.FromSeconds(5);
    private const int MaxSectionChars = 16_000;
    private const int TailBytes = 64 * 1024;

    private readonly IManagedServiceManager _serviceManager;
    private readonly IReadOnlyDictionary<string, ManagedServiceDefinition> _definitions;

    public ServiceDiagnosticsCollector(
        IManagedServiceManager serviceManager,
        IEnumerable<ManagedServiceDefinition> definitions)
    {
        _serviceManager = serviceManager;
        _definitions = definitions.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ServiceDiagnosticsInfo> GetAsync(
        string serviceId,
        string? operation,
        CancellationToken cancellationToken)
    {
        if (!_definitions.TryGetValue(serviceId, out ManagedServiceDefinition? definition))
        {
            throw new KeyNotFoundException($"Service id '{serviceId}' is not managed by this agent.");
        }

        ManagedServiceInfo current;
        try
        {
            current = await _serviceManager.GetAsync(serviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            current = new ManagedServiceInfo
            {
                Id = definition.Id,
                Name = definition.DisplayName,
                Description = definition.Description,
                State = ServiceState.Unknown,
                Ports = definition.Ports,
                Detail = ex.Message
            };
        }

        ServiceDiagnosticsInfo diagnostics = OperatingSystem.IsWindows()
            ? await CollectWindowsAsync(definition, current, operation, cancellationToken).ConfigureAwait(false)
            : OperatingSystem.IsLinux()
                ? await CollectLinuxAsync(definition, current, operation, cancellationToken).ConfigureAwait(false)
                : throw new PlatformNotSupportedException();

        diagnostics.LikelyCause = DetermineLikelyCause(diagnostics);
        return diagnostics;
    }

    private static async Task<ServiceDiagnosticsInfo> CollectWindowsAsync(
        ManagedServiceDefinition definition,
        ManagedServiceInfo current,
        string? operation,
        CancellationToken cancellationToken)
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string scPath = Path.Combine(systemDirectory, "sc.exe");
        string netstatPath = Path.Combine(systemDirectory, "netstat.exe");
        string wevtutilPath = Path.Combine(systemDirectory, "wevtutil.exe");
        string regPath = Path.Combine(systemDirectory, "reg.exe");

        string serviceStatus = await RunTextAsync(
            scPath,
            ["queryex", definition.WindowsServiceName],
            cancellationToken).ConfigureAwait(false);

        string serviceConfig = await RunTextAsync(
            scPath,
            ["qc", definition.WindowsServiceName],
            cancellationToken).ConfigureAwait(false);

        string exitCode = ExtractWindowsExitCode(serviceStatus);
        string? executablePath = ParseWindowsBinaryPath(serviceConfig);

        string portConflicts = await GetWindowsPortConflictsAsync(
            netstatPath,
            definition.Ports,
            current.ProcessId,
            cancellationToken).ConfigureAwait(false);

        string serviceEvents = await GetWindowsServiceEventsAsync(
            wevtutilPath,
            definition,
            cancellationToken).ConfigureAwait(false);

        string configurationTest = string.Empty;
        string applicationLogs = string.Empty;
        if (definition.Id.Equals("nginx", StringComparison.OrdinalIgnoreCase))
        {
            string? nginx = await ResolveWindowsNginxExecutableAsync(
                executablePath,
                definition.WindowsServiceName,
                regPath,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(nginx))
            {
                configurationTest = await RunDiagnosticCommandAsync(
                    nginx,
                    ["-t"],
                    Path.GetDirectoryName(nginx),
                    cancellationToken).ConfigureAwait(false);

                applicationLogs = await ReadFirstExistingTailAsync(
                    GetWindowsNginxLogCandidates(nginx),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                configurationTest = "Nginx executable could not be resolved from the Windows service configuration or common install locations.";
                applicationLogs = await ReadFirstExistingTailAsync(
                    GetWindowsNginxLogCandidates(null),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new ServiceDiagnosticsInfo
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            ServiceId = definition.Id,
            ServiceName = definition.DisplayName,
            Platform = "Windows",
            Operation = operation,
            State = current.State.ToString(),
            ServiceStatus = LimitSection(JoinNonEmpty(
                FormatManagedState(current),
                serviceStatus,
                serviceConfig)),
            ExitCode = exitCode,
            ConfigurationTest = LimitSection(configurationTest),
            PortConflicts = LimitSection(portConflicts),
            RecentServiceLogs = LimitSection(serviceEvents),
            ApplicationLogs = LimitSection(applicationLogs)
        };
    }

    private static async Task<ServiceDiagnosticsInfo> CollectLinuxAsync(
        ManagedServiceDefinition definition,
        ManagedServiceInfo current,
        string? operation,
        CancellationToken cancellationToken)
    {
        string systemctl = FirstExisting("/usr/bin/systemctl", "/bin/systemctl") ?? "systemctl";
        string journalctl = FirstExisting("/usr/bin/journalctl", "/bin/journalctl") ?? "journalctl";

        string serviceStatus = await RunTextAsync(
            systemctl,
            [
                "show",
                "--no-pager",
                "--property=LoadState",
                "--property=ActiveState",
                "--property=SubState",
                "--property=MainPID",
                "--property=Result",
                "--property=ExecMainCode",
                "--property=ExecMainStatus",
                "--property=StatusText",
                "--property=ExecStart",
                definition.LinuxUnitName
            ],
            cancellationToken).ConfigureAwait(false);

        string exitCode = ExtractSystemdExitCode(serviceStatus);
        string? systemdExecutable = ParseSystemdExecStartPath(serviceStatus);

        string serviceLogs = await RunTextAsync(
            journalctl,
            ["--no-pager", "--unit", definition.LinuxUnitName, "--lines", "80", "--output", "short-iso"],
            cancellationToken).ConfigureAwait(false);

        string portConflicts = await GetLinuxPortConflictsAsync(
            definition.Ports,
            current.ProcessId,
            cancellationToken).ConfigureAwait(false);

        string configurationTest = string.Empty;
        string applicationLogs = string.Empty;
        if (definition.Id.Equals("nginx", StringComparison.OrdinalIgnoreCase))
        {
            string? nginx = !string.IsNullOrWhiteSpace(systemdExecutable) &&
                            Path.GetFileName(systemdExecutable).Equals("nginx", StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(systemdExecutable)
                ? systemdExecutable
                : FirstExisting(
                    "/usr/sbin/nginx",
                    "/usr/local/sbin/nginx",
                    "/usr/bin/nginx",
                    "/snap/bin/nginx");

            if (!string.IsNullOrWhiteSpace(nginx))
            {
                configurationTest = await RunDiagnosticCommandAsync(
                    nginx,
                    ["-t"],
                    Path.GetDirectoryName(nginx),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                configurationTest = "Nginx executable was not found in common Linux install locations.";
            }

            applicationLogs = await ReadFirstExistingTailAsync(
                [
                    "/var/log/nginx/error.log",
                    "/usr/local/nginx/logs/error.log",
                    "/opt/nginx/logs/error.log"
                ],
                cancellationToken).ConfigureAwait(false);
        }

        return new ServiceDiagnosticsInfo
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            ServiceId = definition.Id,
            ServiceName = definition.DisplayName,
            Platform = "Linux",
            Operation = operation,
            State = current.State.ToString(),
            ServiceStatus = LimitSection(JoinNonEmpty(FormatManagedState(current), serviceStatus)),
            ExitCode = exitCode,
            ConfigurationTest = LimitSection(configurationTest),
            PortConflicts = LimitSection(portConflicts),
            RecentServiceLogs = LimitSection(serviceLogs),
            ApplicationLogs = LimitSection(applicationLogs)
        };
    }

    private static async Task<string> GetWindowsPortConflictsAsync(
        string netstatPath,
        IReadOnlyList<int> ports,
        int? servicePid,
        CancellationToken cancellationToken)
    {
        if (ports.Count == 0)
        {
            return string.Empty;
        }

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                netstatPath,
                ["-ano", "-p", "tcp"],
                DiagnosticCommandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Unable to inspect TCP listeners: {ex.Message}";
        }

        var conflicts = new List<string>();
        foreach (string rawLine in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || !fields[0].Equals("TCP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!fields[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!int.TryParse(fields[^1], out int pid))
            {
                continue;
            }
            if (servicePid is int ownPid && pid == ownPid)
            {
                continue;
            }

            int? localPort = ParseEndpointPort(fields[1]);
            if (localPort is null || !ports.Contains(localPort.Value))
            {
                continue;
            }

            string processName = TryGetProcessName(pid);
            conflicts.Add($"{localPort.Value}/TCP is listening on {fields[1]} by PID {pid} ({processName}).");
        }

        return conflicts.Count == 0
            ? "No conflicting listener was detected on the managed service ports."
            : string.Join(Environment.NewLine, conflicts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<string> GetLinuxPortConflictsAsync(
        IReadOnlyList<int> ports,
        int? servicePid,
        CancellationToken cancellationToken)
    {
        if (ports.Count == 0)
        {
            return string.Empty;
        }

        string? ss = FirstExisting("/usr/sbin/ss", "/usr/bin/ss", "/bin/ss");
        if (ss is null)
        {
            return "The 'ss' utility is not installed, so listener ownership could not be inspected.";
        }

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                ss,
                ["-ltnp"],
                DiagnosticCommandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Unable to inspect TCP listeners: {ex.Message}";
        }

        var conflicts = new List<string>();
        foreach (string rawLine in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("LISTEN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int? matchedPort = ports.FirstOrDefault(port => EndpointLineContainsPort(line, port));
            if (matchedPort is null or 0)
            {
                continue;
            }

            if (servicePid is int ownPid && line.Contains($"pid={ownPid},", StringComparison.Ordinal))
            {
                continue;
            }

            conflicts.Add(line);
        }

        return conflicts.Count == 0
            ? "No conflicting listener was detected on the managed service ports."
            : string.Join(Environment.NewLine, conflicts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<string> GetWindowsServiceEventsAsync(
        string wevtutilPath,
        ManagedServiceDefinition definition,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(
                wevtutilPath,
                [
                    "qe",
                    "System",
                    "/q:*[System[Provider[@Name='Service Control Manager']]]",
                    "/c:80",
                    "/rd:true",
                    "/f:text"
                ],
                DiagnosticCommandTimeout,
                cancellationToken).ConfigureAwait(false);

            string output = JoinProcessOutput(result);
            if (string.IsNullOrWhiteSpace(output))
            {
                return "No recent Service Control Manager events were returned.";
            }

            string[] blocks = output.Split(
                new[] { "Event[" },
                StringSplitOptions.RemoveEmptyEntries);
            string[] matches = blocks
                .Where(block =>
                    block.Contains(definition.WindowsServiceName, StringComparison.OrdinalIgnoreCase) ||
                    block.Contains(definition.DisplayName, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .Select(block => "Event[" + block.Trim())
                .ToArray();

            return matches.Length == 0
                ? "No recent Service Control Manager event mentioning this managed service was found."
                : string.Join(Environment.NewLine + Environment.NewLine, matches);
        }
        catch (Exception ex)
        {
            return $"Unable to read Service Control Manager events: {ex.Message}";
        }
    }

    private static async Task<string> RunTextAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(
                executable,
                arguments,
                DiagnosticCommandTimeout,
                cancellationToken).ConfigureAwait(false);
            return JoinProcessOutput(result);
        }
        catch (Exception ex)
        {
            return $"Diagnostic command '{Path.GetFileName(executable)}' failed: {ex.Message}";
        }
    }

    private static async Task<string> RunDiagnosticCommandAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(
                executable,
                arguments,
                DiagnosticCommandTimeout,
                cancellationToken,
                workingDirectory).ConfigureAwait(false);

            string text = JoinProcessOutput(result);
            return $"Exit code: {result.ExitCode}{Environment.NewLine}{text}".Trim();
        }
        catch (Exception ex)
        {
            return $"Configuration test could not be completed: {ex.Message}";
        }
    }

    private static string JoinProcessOutput(ProcessResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            parts.Add(result.StandardOutput.Trim());
        }
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            parts.Add(result.StandardError.Trim());
        }
        if (parts.Count == 0 && result.ExitCode != 0)
        {
            parts.Add($"Command exited with code {result.ExitCode}.");
        }
        return string.Join(Environment.NewLine, parts);
    }

    private static string ExtractWindowsExitCode(string serviceStatus)
    {
        string win32 = ExtractColonValue(serviceStatus, "WIN32_EXIT_CODE");
        string service = ExtractColonValue(serviceStatus, "SERVICE_EXIT_CODE");
        return JoinNonEmpty(
            string.IsNullOrWhiteSpace(win32) ? null : $"Win32 exit code: {win32}",
            string.IsNullOrWhiteSpace(service) ? null : $"Service-specific exit code: {service}");
    }

    private static string? ParseSystemdExecStartPath(string serviceStatus)
    {
        Dictionary<string, string> values = ParseEqualsLines(serviceStatus);
        if (!values.TryGetValue("ExecStart", out string? execStart) || string.IsNullOrWhiteSpace(execStart))
        {
            return null;
        }

        const string pathMarker = "path=";
        int pathIndex = execStart.IndexOf(pathMarker, StringComparison.OrdinalIgnoreCase);
        if (pathIndex >= 0)
        {
            int start = pathIndex + pathMarker.Length;
            int end = execStart.IndexOfAny(new[] { ' ', ';', '}' }, start);
            string value = end > start ? execStart[start..end] : execStart[start..];
            return value.Trim().Trim('"');
        }

        return null;
    }

    private static string ExtractSystemdExitCode(string serviceStatus)
    {
        Dictionary<string, string> values = ParseEqualsLines(serviceStatus);
        values.TryGetValue("Result", out string? result);
        values.TryGetValue("ExecMainCode", out string? code);
        values.TryGetValue("ExecMainStatus", out string? status);
        return JoinNonEmpty(
            string.IsNullOrWhiteSpace(result) ? null : $"Result: {result}",
            string.IsNullOrWhiteSpace(code) ? null : $"ExecMainCode: {code}",
            string.IsNullOrWhiteSpace(status) ? null : $"ExecMainStatus: {status}");
    }

    private static string DetermineLikelyCause(ServiceDiagnosticsInfo diagnostics)
    {
        bool portConflictCanExplainFailure =
            !diagnostics.State.Equals(ServiceState.Running.ToString(), StringComparison.OrdinalIgnoreCase) ||
            diagnostics.Operation is null ||
            diagnostics.Operation.Equals("start", StringComparison.OrdinalIgnoreCase) ||
            diagnostics.Operation.Equals("restart", StringComparison.OrdinalIgnoreCase);
        string portLine = FirstMeaningfulProblemLine(
            diagnostics.PortConflicts,
            new[] { "No conflicting listener", "Unable to inspect", "not installed", "could not be inspected" });
        if (portConflictCanExplainFailure && !string.IsNullOrWhiteSpace(portLine))
        {
            return $"A required TCP port appears to be owned by another process: {portLine}";
        }

        string configLine = FirstMeaningfulProblemLine(
            diagnostics.ConfigurationTest,
            new[] { "syntax is ok", "test is successful", "Exit code:", "could not be resolved", "was not found" });
        if (!string.IsNullOrWhiteSpace(configLine) &&
            (diagnostics.ConfigurationTest.Contains("Exit code: 1", StringComparison.OrdinalIgnoreCase) ||
             diagnostics.ConfigurationTest.Contains("[emerg]", StringComparison.OrdinalIgnoreCase) ||
             diagnostics.ConfigurationTest.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
             diagnostics.ConfigurationTest.Contains("error", StringComparison.OrdinalIgnoreCase)))
        {
            return $"The service configuration test reported an error: {configLine}";
        }

        string applicationLine = FindErrorLine(diagnostics.ApplicationLogs);
        if (!string.IsNullOrWhiteSpace(applicationLine))
        {
            return $"The application error log reports: {applicationLine}";
        }

        string serviceLogLine = FindErrorLine(diagnostics.RecentServiceLogs);
        if (!string.IsNullOrWhiteSpace(serviceLogLine))
        {
            return $"The operating-system service log reports: {serviceLogLine}";
        }

        if (!string.IsNullOrWhiteSpace(diagnostics.ExitCode) &&
            !diagnostics.ExitCode.Contains(" 0", StringComparison.Ordinal))
        {
            return $"The service exited unsuccessfully. {diagnostics.ExitCode.Replace(Environment.NewLine, "; ")}";
        }

        return "No single root cause was identified from the available service state, port ownership, configuration test, and recent logs.";
    }

    private static string FindErrorLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] indicators = ["[emerg]", "error", "failed", "failure", "cannot", "can't", "denied", "bind()", "address already in use", "fatal"];
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse()
            .FirstOrDefault(line => indicators.Any(indicator => line.Contains(indicator, StringComparison.OrdinalIgnoreCase)))
            ?? string.Empty;
    }

    private static string FirstMeaningfulProblemLine(string text, IEnumerable<string> ignoredIndicators)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !ignoredIndicators.Any(ignored => line.Contains(ignored, StringComparison.OrdinalIgnoreCase)))
            ?? string.Empty;
    }

    private static string FormatManagedState(ManagedServiceInfo current) =>
        JoinNonEmpty(
            $"Managed state: {current.State}",
            current.ProcessId is int pid ? $"PID: {pid}" : null,
            current.UptimeSeconds is long uptime ? $"Uptime seconds: {uptime}" : null,
            string.IsNullOrWhiteSpace(current.Detail) ? null : $"Detail: {current.Detail}");

    private static string? ParseWindowsBinaryPath(string scConfig)
    {
        string value = ExtractColonValue(scConfig, "BINARY_PATH_NAME");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = Environment.ExpandEnvironmentVariables(value.Trim());
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            if (end > 1)
            {
                return value[1..end];
            }
        }

        // Prefer an existing prefix ending in .exe so unquoted paths containing spaces still work.
        int exeIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            string candidate = value[..(exeIndex + 4)].Trim();
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;
        return token.Trim('"');
    }

    private static async Task<string?> ResolveWindowsNginxExecutableAsync(
        string? serviceExecutable,
        string windowsServiceName,
        string regPath,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(serviceExecutable) &&
            Path.GetFileName(serviceExecutable).Equals("nginx.exe", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(serviceExecutable);
        }

        // NSSM-backed Nginx services store the actual application path under
        // Services\<name>\Parameters\Application. Query it explicitly rather
        // than trying to execute nssm.exe as if it were nginx.exe.
        try
        {
            ProcessResult registryResult = await ProcessRunner.RunAsync(
                regPath,
                [
                    "query",
                    $@"HKLM\SYSTEM\CurrentControlSet\Services\{windowsServiceName}\Parameters",
                    "/v",
                    "Application"
                ],
                DiagnosticCommandTimeout,
                cancellationToken).ConfigureAwait(false);

            foreach (string line in registryResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int typeIndex = line.IndexOf("REG_", StringComparison.OrdinalIgnoreCase);
                if (typeIndex < 0)
                {
                    continue;
                }
                int valueStart = line.IndexOf(' ', typeIndex);
                while (valueStart >= 0 && valueStart < line.Length && char.IsWhiteSpace(line[valueStart]))
                {
                    valueStart++;
                }
                if (valueStart > typeIndex && valueStart < line.Length)
                {
                    string candidate = Environment.ExpandEnvironmentVariables(line[valueStart..].Trim().Trim('"'));
                    if (Path.GetFileName(candidate).Equals("nginx.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }
        catch
        {
            // NSSM metadata is optional; common locations below remain useful.
        }

        candidates.Add(@"C:\nginx\nginx.exe");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nginx", "nginx.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nginx", "nginx.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> GetWindowsNginxLogCandidates(string? nginxExecutable)
    {
        if (!string.IsNullOrWhiteSpace(nginxExecutable))
        {
            string? directory = Path.GetDirectoryName(nginxExecutable);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return Path.Combine(directory, "logs", "error.log");
                yield return Path.Combine(directory, "error.log");
            }
        }
        yield return @"C:\nginx\logs\error.log";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "nginx", "logs", "error.log");
    }

    private static async Task<string> ReadFirstExistingTailAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                string text = await ReadTailAsync(path, cancellationToken).ConfigureAwait(false);
                return $"{path}{Environment.NewLine}{text}".Trim();
            }
            catch (Exception ex)
            {
                return $"Unable to read {path}: {ex.Message}";
            }
        }

        return string.Empty;
    }

    private static async Task<string> ReadTailAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (start > 0)
        {
            int firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0 && firstNewline + 1 < text.Length)
            {
                text = text[(firstNewline + 1)..];
            }
        }
        return LimitSection(text.Trim());
    }

    private static string ExtractColonValue(string text, string key)
    {
        string? line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return string.Empty;
        }
        int colon = line.IndexOf(':');
        return colon >= 0 ? line[(colon + 1)..].Trim() : line.Trim();
    }

    private static Dictionary<string, string> ParseEqualsLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);

    private static int? ParseEndpointPort(string endpoint)
    {
        int colon = endpoint.LastIndexOf(':');
        if (colon < 0 || colon + 1 >= endpoint.Length)
        {
            return null;
        }
        return int.TryParse(endpoint[(colon + 1)..], out int port) ? port : null;
    }

    private static bool EndpointLineContainsPort(string line, int port)
    {
        string marker = $":{port}";
        int index = line.IndexOf(marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            int after = index + marker.Length;
            if (after == line.Length || char.IsWhiteSpace(line[after]))
            {
                return true;
            }
            index = line.IndexOf(marker, index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static string TryGetProcessName(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "unknown process";
        }
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static string JoinNonEmpty(params string?[] values) =>
        string.Join(Environment.NewLine, values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()));

    private static string LimitSection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        string text = value.Trim();
        return text.Length <= MaxSectionChars ? text : text[^MaxSectionChars..];
    }
}
