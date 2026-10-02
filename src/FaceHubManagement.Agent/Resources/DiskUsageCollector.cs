namespace FaceHubManagement.Agent.Resources;

internal static class DiskUsageCollector
{
    public static (ulong Used, ulong Total) GetSystemDiskUsage()
    {
        try
        {
            string rootPath = OperatingSystem.IsWindows()
                ? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\"
                : "/";

            var drive = new DriveInfo(rootPath);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return (0, 0);
            }

            ulong total = checked((ulong)drive.TotalSize);
            ulong available = drive.TotalFreeSpace > 0
                ? checked((ulong)drive.TotalFreeSpace)
                : 0;
            ulong used = total - Math.Min(total, available);
            return (used, total);
        }
        catch
        {
            // Resource telemetry must remain available even if the system drive
            // cannot be queried (for example, a transient mount/filesystem issue).
            return (0, 0);
        }
    }
}
