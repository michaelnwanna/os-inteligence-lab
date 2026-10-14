using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ail.Schemas;

public static class Dimensions
{
    /// <summary>Dimensions along which populations are segmented into cohorts.</summary>
    public static readonly string[] All =
    {
        "os_version", "architecture", "device_model", "application", "application_version", "driver_version",
    };

    public static int IndexOf(string dimension) => Array.IndexOf(All, dimension);
}

/// <summary>Minute buckets: integer minutes since the Unix epoch (UTC).</summary>
public static class TimeUtil
{
    public static long BucketOf(DateTimeOffset dt) => (long)Math.Floor(dt.ToUnixTimeSeconds() / 60.0);
    public static DateTimeOffset DtOf(long bucket) => DateTimeOffset.FromUnixTimeSeconds(bucket * 60);
    public static string Hhmm(long bucket) => DtOf(bucket).ToString("HH:mm");
    public static string Iso(long bucket) => DtOf(bucket).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
}

/// <summary>One telemetry record. No personal-data fields are defined or accepted.</summary>
public sealed class TelemetryEvent
{
    private static readonly HashSet<string> PiiTokens = new()
    {
        "email", "username", "user", "ip", "phone", "address", "ssn", "name", "firstname", "lastname",
        "hostname", "mac", "imei", "serial",
    };

    private static readonly HashSet<string> EventTypes = new() { "session", "crash", "metric" };

    public string? EventId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? DeviceId { get; set; }
    public string? SessionId { get; set; }
    public string? Os { get; set; }
    public string? OsVersion { get; set; }
    public string? OsBuild { get; set; }
    public string? Architecture { get; set; }
    public string? DeviceModel { get; set; }
    public string? Cpu { get; set; }
    public double? MemoryGb { get; set; }
    public string? Application { get; set; }
    public string? ApplicationVersion { get; set; }
    public string? Driver { get; set; }
    public string? DriverVersion { get; set; }
    public string? EventType { get; set; }
    public string? MetricName { get; set; }
    public double? MetricValue { get; set; }
    public string? FailureSignature { get; set; }
    public string? DeploymentId { get; set; }
    public Dictionary<string, string> Dimensions { get; set; } = new();

    /// <summary>Returns validation errors; an empty list means the event is acceptable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Required(string? v, string name, int max)
        {
            if (string.IsNullOrWhiteSpace(v)) errors.Add($"{name} is required");
            else if (v.Length > max) errors.Add($"{name} is too long");
        }

        if (Timestamp == default) errors.Add("timestamp is required");
        Required(DeviceId, "device_id", 128);
        Required(SessionId, "session_id", 128);
        Required(Os, "os", 64);
        Required(OsVersion, "os_version", 64);
        Required(Architecture, "architecture", 32);
        Required(DeviceModel, "device_model", 64);
        Required(Application, "application", 64);
        Required(ApplicationVersion, "application_version", 64);
        if (EventType is null || !EventTypes.Contains(EventType)) errors.Add("event_type must be session, crash or metric");
        if (EventType == "metric")
        {
            if (string.IsNullOrEmpty(MetricName) || MetricValue is null) errors.Add("metric events require metric_name and metric_value");
            else if (!double.IsFinite(MetricValue.Value)) errors.Add("metric_value must be finite");
        }
        if (Dimensions.Count > 16) errors.Add("at most 16 custom dimensions are allowed");
        foreach (var key in Dimensions.Keys)
        {
            var tokens = Regex.Split(key.ToLowerInvariant(), "[^a-z0-9]+");
            if (tokens.Any(PiiTokens.Contains)) errors.Add($"dimension key '{key}' looks like personal data and is not accepted");
        }
        return errors;
    }

    /// <summary>Stable id used to drop duplicate deliveries of the same event.</summary>
    public string ResolvedEventId()
    {
        if (!string.IsNullOrEmpty(EventId)) return EventId;
        var raw = string.Join("|", Timestamp.UtcDateTime.ToString("O"), DeviceId, SessionId, EventType, MetricName ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..32];
    }
}

/// <summary>A change to the environment that may explain a regression.</summary>
public sealed class ChangeEvent
{
    public static readonly HashSet<string> Kinds = new()
    {
        "app_deployment", "driver_update", "os_update", "config_change", "dependency_update",
        "infrastructure_change", "git_commit",
    };

    public string DeploymentId { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public string Kind { get; set; } = "";
    public string Component { get; set; } = "";
    public string? Dimension { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public string Description { get; set; } = "";
    public string? Repository { get; set; }
    public string? BaseRef { get; set; }
    public string? HeadRef { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(DeploymentId) || DeploymentId.Length > 128) errors.Add("deployment_id is required");
        if (Timestamp == default) errors.Add("timestamp is required");
        if (!Kinds.Contains(Kind)) errors.Add("kind is not recognised");
        if (string.IsNullOrWhiteSpace(Component)) errors.Add("component is required");
        if (Dimension is not null && Schemas.Dimensions.IndexOf(Dimension) < 0) errors.Add("dimension must be a known dimension");
        return errors;
    }
}
