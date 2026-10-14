using System.Text.Json;
using Ail.Schemas;

namespace Ail.Agent;

public sealed class Consent
{
    public List<string> Signals { get; set; } = new();
    public string Endpoint { get; set; } = "";
    /// <summary>Random, generated locally; not derived from hardware or user identity.</summary>
    public string DeviceId { get; set; } = "";
    public string GrantedAt { get; set; } = "";
}

/// <summary>Explicit, revocable consent. Without a consent file the agent refuses to run. Same file format as the Python agent.</summary>
public sealed class ConsentStore
{
    private static readonly JsonSerializerOptions Indented = new(AilJson.Options) { WriteIndented = true };

    public string Dir { get; }
    private string FilePath => Path.Combine(Dir, "consent.json");

    public ConsentStore(string? dir = null)
    {
        var home = Environment.GetEnvironmentVariable("AIL_AGENT_HOME");
        Dir = dir ?? (!string.IsNullOrEmpty(home) ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ail-agent"));
    }

    public Consent? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var consent = JsonSerializer.Deserialize<Consent>(File.ReadAllText(FilePath), AilJson.Options);
            return consent is not null && consent.Signals.All(Collectors.Signals.ContainsKey) ? consent : null;
        }
        catch (JsonException) { return null; }
    }

    public Consent Grant(IEnumerable<string> signals, string endpoint)
    {
        var chosen = signals.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (chosen.Count == 0 || chosen.Any(s => !Collectors.Signals.ContainsKey(s)))
            throw new ArgumentException($"signals must be a non-empty subset of [{string.Join(", ", Collectors.Signals.Keys.Order(StringComparer.Ordinal))}]");
        var consent = new Consent
        {
            Signals = chosen, Endpoint = endpoint, DeviceId = Load()?.DeviceId ?? Guid.NewGuid().ToString("N"),
            GrantedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(consent, Indented));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return consent;
    }

    /// <summary>Delete consent and the local device id.</summary>
    public bool Revoke()
    {
        var existed = File.Exists(FilePath);
        if (existed) File.Delete(FilePath);
        return existed;
    }
}
