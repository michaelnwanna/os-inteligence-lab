using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Ail.Agent;

/// <summary>Coarse, non-identifying machine description. No hostname, serial or user name.</summary>
public sealed record SystemInfo(string Os, string OsVersion, string Architecture, string DeviceModel, string Cpu, double MemoryGb);

/// <summary>Signal collectors, one implementation per operating system. Everything collected is listed in <see cref="Signals"/>.</summary>
public static partial class Collectors
{
    public static readonly IReadOnlyDictionary<string, string> Signals = new Dictionary<string, string>
    {
        ["session_heartbeat"] = "One 'session' event per interval so failure-rate denominators exist. No content.",
        ["cpu_load_pct"] = "System CPU load, 0-100 (load average / cores on Linux and macOS, CPU time deltas on Windows).",
        ["memory_mb"] = "System memory in use, in MB (used = total minus available).",
    };

    [GeneratedRegex(@"^(\w+):\s+(\d+)\s*kB", RegexOptions.Multiline)]
    private static partial Regex MeminfoLine();

    [GeneratedRegex(@"page size of (\d+) bytes")]
    private static partial Regex VmPageSize();

    [GeneratedRegex(@"^Pages ([\w \-]+):\s+(\d+)\.", RegexOptions.Multiline)]
    private static partial Regex VmPages();

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex FirstNumber();

    /// <summary>Linux /proc/meminfo to (used_mb, total_mb).</summary>
    public static (double UsedMb, double TotalMb) ParseMeminfo(string text)
    {
        var kb = MeminfoLine().Matches(text).ToDictionary(m => m.Groups[1].Value, m => long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        var total = kb["MemTotal"];
        var available = kb.TryGetValue("MemAvailable", out var a) ? a : kb.GetValueOrDefault("MemFree");
        return ((total - available) / 1024.0, total / 1024.0);
    }

    /// <summary>macOS `vm_stat` output plus `hw.memsize` to (used_mb, total_mb).</summary>
    public static (double UsedMb, double TotalMb) ParseVmStat(string text, long memBytes)
    {
        var page = long.Parse(VmPageSize().Match(text).Groups[1].Value, CultureInfo.InvariantCulture);
        var pages = VmPages().Matches(text).ToDictionary(m => m.Groups[1].Value, m => long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        var used = pages.GetValueOrDefault("active") + pages.GetValueOrDefault("wired down") + pages.GetValueOrDefault("occupied by compressor");
        return (used * page / 1048576.0, memBytes / 1048576.0);
    }

    /// <summary>First number of `/proc/loadavg` ("0.52 0.58 ...") or `sysctl -n vm.loadavg` ("{ 1.23 1.45 1.67 }").</summary>
    public static double ParseLoadAverage(string text) =>
        double.Parse(FirstNumber().Match(text).Value, CultureInfo.InvariantCulture);

    private static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {file}");
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(5000)) { p.Kill(); throw new InvalidOperationException($"{file} timed out"); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} exited with {p.ExitCode}");
        return output;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, Load;
        public ulong Total, Avail, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    /// <summary>(used_mb, total_mb) for the current machine.</summary>
    public static (double UsedMb, double TotalMb) MemoryMb()
    {
        if (OperatingSystem.IsLinux()) return ParseMeminfo(File.ReadAllText("/proc/meminfo"));
        if (OperatingSystem.IsMacOS())
            return ParseVmStat(Run("vm_stat"), long.Parse(Run("sysctl", "-n", "hw.memsize").Trim(), CultureInfo.InvariantCulture));
        if (OperatingSystem.IsWindows())
        {
            var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref s)) throw new InvalidOperationException("GlobalMemoryStatusEx failed");
            return ((s.Total - s.Avail) / 1048576.0, s.Total / 1048576.0);
        }
        throw new PlatformNotSupportedException("unsupported platform");
    }

    private static (ulong Idle, ulong Total) WindowsTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new InvalidOperationException("GetSystemTimes failed");
        return (idle, kernel + user);
    }

    public static double CpuLoadPct(double sampleSeconds = 0.5)
    {
        double pct;
        if (OperatingSystem.IsWindows())
        {
            var (i0, t0) = WindowsTimes();
            Thread.Sleep(TimeSpan.FromSeconds(sampleSeconds));
            var (i1, t1) = WindowsTimes();
            pct = 100.0 * (1 - (double)(i1 - i0) / Math.Max(t1 - t0, 1UL));
        }
        else
        {
            var text = OperatingSystem.IsLinux() ? File.ReadAllText("/proc/loadavg") : Run("sysctl", "-n", "vm.loadavg");
            pct = ParseLoadAverage(text) / Environment.ProcessorCount * 100;
        }
        return Math.Clamp(pct, 0.0, 100.0);
    }

    private static string OsVersion()
    {
        var v = Environment.OSVersion.Version;
        if (OperatingSystem.IsMacOS()) return Run("sw_vers", "-productVersion").Trim();
        if (OperatingSystem.IsWindows()) return v.Major == 10 ? (v.Build >= 22000 ? "11" : "10") : v.Major.ToString(CultureInfo.InvariantCulture);
        if (OperatingSystem.IsLinux() && File.Exists("/proc/sys/kernel/osrelease")) return File.ReadAllText("/proc/sys/kernel/osrelease").Trim();
        return v.ToString();
    }

    public static SystemInfo GetSystemInfo()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64", Architecture.Arm64 => "arm64", Architecture.X86 => "x86",
            var other => other.ToString().ToLowerInvariant(),
        };
        var name = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription;
        var (_, total) = MemoryMb();
        var version = OsVersion();
        return new SystemInfo(name, version.Length > 0 ? version : "unknown", arch, $"generic-{arch}", $"{arch}-{Environment.ProcessorCount}c", Math.Round(total / 1024));
    }

    public static double? Collect(string signal) => signal switch
    {
        "cpu_load_pct" => CpuLoadPct(),
        "memory_mb" => MemoryMb().UsedMb,
        _ => null,
    };
}
