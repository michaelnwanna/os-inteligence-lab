using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ail.Simulator;

public sealed class GaugeSpec
{
    public double Mean { get; set; }
    public double Std { get; set; }
}

public sealed class Population
{
    public int Devices { get; set; } = 100_000;
    public string Os { get; set; } = "AtlasOS";
    public string Application { get; set; } = "Atlas";
    public Dictionary<string, double> Architectures { get; set; } = new() { ["x64"] = 0.7, ["arm64"] = 0.2, ["x86"] = 0.1 };
    public Dictionary<string, List<string>> Models { get; set; } = new()
    {
        ["x64"] = new() { "Aero-X1", "Aero-X2", "Titan-15", "Titan-17" },
        ["arm64"] = new() { "Nova-A1", "Nova-A2" },
        ["x86"] = new() { "Legacy-R1", "Legacy-R2" },
    };
    public Dictionary<string, double> OsVersions { get; set; } = new() { ["12.4"] = 0.5, ["12.5"] = 0.35, ["13.0"] = 0.15 };
    public Dictionary<string, double> AppVersions { get; set; } = new() { ["2.0"] = 1.0 };
    public Dictionary<string, double> Drivers { get; set; } = new() { ["7.12"] = 0.5, ["7.13"] = 0.5 };
}

public sealed class Baseline
{
    public double FailureRate { get; set; } = 0.004;
    public double ActiveSessionRate { get; set; } = 0.05;
    public GaugeSpec LatencyMs { get; set; } = new() { Mean = 240, Std = 80 };
    public GaugeSpec MemoryMb { get; set; } = new() { Mean = 410, Std = 90 };
    public double ModelSpread { get; set; } = 0.15;
    public Dictionary<string, double> Signatures { get; set; } = new()
    {
        ["UNHANDLED_EXCEPTION|Core.Dispatch"] = 0.40,
        ["TIMEOUT|Net.Fetch"] = 0.30,
        ["ACCESS_VIOLATION|Render.Draw"] = 0.20,
        ["WATCHDOG_RESET|Sys.Idle"] = 0.10,
    };
}

/// <summary>A staged deployment that moves devices from FromVersion to ToVersion.</summary>
public sealed class Rollout
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "app_deployment";
    public string Component { get; set; } = "";
    public string Dimension { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public int AtMinute { get; set; }
    public int RampMinutes { get; set; }
    public double MaxExposure { get; set; } = 1.0;
    public string Description { get; set; } = "";
    public string? Repository { get; set; }
    public string? BaseRef { get; set; }
    public string? HeadRef { get; set; }
}

public sealed class ChangeSpec
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "config_change";
    public string Component { get; set; } = "";
    public int AtMinute { get; set; }
    public string Description { get; set; } = "";
}

/// <summary>A regression injected into every cell matching Where.</summary>
public sealed class Effect
{
    public string Id { get; set; } = "";
    public Dictionary<string, object> Where { get; set; } = new();
    public int StartMinute { get; set; }
    public int? EndMinute { get; set; }
    public int RampMinutes { get; set; }
    public double? FailureRate { get; set; }
    public double LatencyMultiplier { get; set; } = 1.0;
    public double MemoryMultiplier { get; set; } = 1.0;
    public string? Signature { get; set; }
    public string Description { get; set; } = "";
}

public sealed class Traffic
{
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
    public double Multiplier { get; set; }
}

public sealed class Episode
{
    public int EffectStartMinute { get; set; }
    public Dictionary<string, string>? Cohort { get; set; }
    public string? Trigger { get; set; }
    public string? Metric { get; set; }
    public string? CulpritCommit { get; set; }
}

public sealed class Expected
{
    public int Incidents { get; set; }
    public int? EffectStartMinute { get; set; }
    public Dictionary<string, string>? Cohort { get; set; }
    public string? Trigger { get; set; }
    public string? Metric { get; set; }
    public string? CulpritCommit { get; set; }
    public List<Episode> Episodes { get; set; } = new();

    public List<Episode> AllEpisodes()
    {
        if (Episodes.Count > 0) return Episodes;
        if (Incidents > 0 && EffectStartMinute is not null)
            return new() { new Episode { EffectStartMinute = EffectStartMinute.Value, Cohort = Cohort, Trigger = Trigger, Metric = Metric, CulpritCommit = CulpritCommit } };
        return new();
    }
}

public sealed class Scenario
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Seed { get; set; } = 1;
    public DateTimeOffset Start { get; set; } = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    public int DurationMinutes { get; set; } = 180;
    public Population Population { get; set; } = new();
    public Baseline Baseline { get; set; } = new();
    public List<Rollout> Rollouts { get; set; } = new();
    public List<ChangeSpec> Changes { get; set; } = new();
    public List<Effect> Effects { get; set; } = new();
    public List<Traffic> Traffic { get; set; } = new();
    public Expected Expected { get; set; } = new();

    public string ConfigHash()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, Schemas.AilJson.Options);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant()[..12];
    }
}

public static class ScenarioLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Repository root: AIL_HOME if set, otherwise the nearest parent that contains docs/scenarios.</summary>
    public static string RepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("AIL_HOME");
        if (!string.IsNullOrEmpty(env)) return env;
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "docs", "scenarios"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("could not find docs/scenarios; set AIL_HOME to the repository root");
    }

    public static string ScenarioDir() => Path.Combine(RepoRoot(), "docs", "scenarios");

    /// <summary>Load by file path, id (scenario-003) or id prefix.</summary>
    public static Scenario Load(string reference)
    {
        var path = File.Exists(reference)
            ? reference
            : Directory.GetFiles(ScenarioDir(), $"{reference}*.yaml").OrderBy(p => p).FirstOrDefault()
              ?? throw new FileNotFoundException($"no scenario matching '{reference}' in {ScenarioDir()}");
        return Yaml.Deserialize<Scenario>(File.ReadAllText(path));
    }

    /// <summary>Scenarios written after the detector settings were fixed; never used for tuning.</summary>
    public static List<Scenario> LoadHeldout() =>
        Directory.GetFiles(Path.Combine(ScenarioDir(), "heldout"), "heldout-*.yaml").OrderBy(p => p)
            .Select(p => Yaml.Deserialize<Scenario>(File.ReadAllText(p))).ToList();

    public static List<Scenario> LoadAll() =>
        Directory.GetFiles(ScenarioDir(), "scenario-*.yaml").OrderBy(p => p).Select(p => Yaml.Deserialize<Scenario>(File.ReadAllText(p))).ToList();
}
