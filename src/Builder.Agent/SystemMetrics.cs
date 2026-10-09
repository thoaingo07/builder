using System.Globalization;

namespace Builder.Agent;

/// <summary>CPU / memory / disk usage. Linux reads /proc; other systems report what .NET exposes.</summary>
public sealed class SystemMetrics(string diskPath)
{
    private (long Idle, long Total)? _lastCpu;

    public double CpuPercent()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/stat")) return 0;
        var parts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var idle = parts[3] + (parts.Length > 4 ? parts[4] : 0); // idle + iowait
        var total = parts.Sum();
        var previous = _lastCpu;
        _lastCpu = (idle, total);
        if (previous is not { } p || total == p.Total) return 0;
        return Math.Clamp(100.0 * (1 - (double)(idle - p.Idle) / (total - p.Total)), 0, 100);
    }

    public (long Total, long Used) Memory()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            var info = File.ReadLines("/proc/meminfo")
                .Select(l => l.Split(':', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => long.Parse(p[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture) * 1024);
            var total = info.GetValueOrDefault("MemTotal");
            var available = info.GetValueOrDefault("MemAvailable", info.GetValueOrDefault("MemFree"));
            return (total, total - available);
        }
        var gc = GC.GetGCMemoryInfo();
        return (gc.TotalAvailableMemoryBytes, gc.MemoryLoadBytes);
    }

    public (long Total, long Used) Disk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(diskPath)) ?? "/");
            // statfs of the mount the work directory lives on
            var mount = DriveInfo.GetDrives()
                .Where(d => d.IsReady && Path.GetFullPath(diskPath).StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault() ?? drive;
            return (mount.TotalSize, mount.TotalSize - mount.AvailableFreeSpace);
        }
        catch
        {
            return (0, 0);
        }
    }

    public static double LoadAverage()
    {
        if (!File.Exists("/proc/loadavg")) return 0;
        return double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0], CultureInfo.InvariantCulture);
    }
}
