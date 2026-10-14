using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ail.Schemas;
using Microsoft.Data.Sqlite;

namespace Ail.Investigation;

/// <summary>Historical incident retrieval and recurring-regression detection.</summary>
public static class Similarity
{
    public static readonly Dictionary<string, double> Weights = new() { ["metric"] = 0.15, ["cohort"] = 0.30, ["signature"] = 0.35, ["trigger"] = 0.20 };
    public const double RecurrenceScore = 0.8;

    private static double Num(object? v) => v switch { double d => d, JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetDouble(), _ => 0.0 };
    private static string Str(object? v) => v switch { string s => s, JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString(), _ => v?.ToString() ?? "" };

    /// <summary>'OOM_KILL|MemoryCache.Allocate' becomes {oom, kill, memorycache, allocate}.</summary>
    public static HashSet<string> SignatureTokens(string signature) =>
        Regex.Split(signature.ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 1).ToHashSet();

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        var union = a.Union(b).Count();
        return union == 0 ? 0.0 : (double)a.Intersect(b).Count() / union;
    }

    private static HashSet<string> SignatureSet(Incident inc)
    {
        var tokens = new HashSet<string>();
        foreach (var s in inc.Signatures)
            if (Num(s["excess_share"]) >= 0.2) tokens.UnionWith(SignatureTokens(Str(s["signature"])));
        return tokens;
    }

    private static (string, string)? Trigger(Incident inc)
    {
        var top = inc.Correlations.FirstOrDefault(c => !c.AfterOnset && c.Score >= 0.5);
        return top is null ? null : (top.Change.Kind, top.Change.Component);
    }

    private static HashSet<string> CohortSet(Incident inc) =>
        inc.Cohorts.Count > 0 ? inc.Cohorts[0].Dimensions.Select(kv => $"{kv.Key}={kv.Value}").ToHashSet() : new();

    public static (double Score, Dictionary<string, double> Parts) Compare(Incident a, Incident b)
    {
        HashSet<string> ca = CohortSet(a), cb = CohortSet(b);
        var ta = Trigger(a);
        var parts = new Dictionary<string, double>
        {
            ["metric"] = a.Metric == b.Metric ? 1.0 : 0.0,
            ["cohort"] = ca.Count > 0 || cb.Count > 0 ? Jaccard(ca, cb) : 1.0,
            ["signature"] = Jaccard(SignatureSet(a), SignatureSet(b)),
            ["trigger"] = ta is not null && ta == Trigger(b) ? 1.0 : 0.0,
        };
        return (Math.Round(parts.Sum(kv => Weights[kv.Key] * kv.Value), 4), parts);
    }
}

/// <summary>Incidents from earlier analyses, kept across runs and databases.</summary>
public sealed class IncidentArchive : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public IncidentArchive(string path = ":memory:")
    {
        if (path != ":memory:" && Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS archive (key TEXT PRIMARY KEY, data_fp TEXT, payload TEXT)";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() { lock (_lock) _conn.Dispose(); }

    private static string Fingerprint(Incident i) => i.Reproducibility.TryGetValue("data_fingerprint", out var v) ? v?.ToString() ?? "" : "";

    public void Add(IEnumerable<Incident> incidents)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var i in incidents)
            {
                var fp = Fingerprint(i);
                var slim = JsonSerializer.SerializeToNode(i, AilJson.Options)!.AsObject();
                slim.Remove("investigation");
                slim.Remove("timemachine");
                slim["timeline"] = new JsonArray();
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO archive VALUES ($k, $fp, $p)";
                cmd.Parameters.AddWithValue("$k", $"{fp}:{i.Id}");
                cmd.Parameters.AddWithValue("$fp", fp);
                cmd.Parameters.AddWithValue("$p", slim.ToJsonString(AilJson.Options));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<Incident> All(string? excludeData = null)
    {
        var payloads = new List<string>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM archive WHERE data_fp != $fp ORDER BY key";
            cmd.Parameters.AddWithValue("$fp", excludeData ?? "");
            using var r = cmd.ExecuteReader();
            while (r.Read()) payloads.Add(r.GetString(0));
        }
        return payloads.Select(p => JsonSerializer.Deserialize<Incident>(p, AilJson.Options)!).ToList();
    }

    public List<Dictionary<string, object?>> FindSimilar(Incident inc, int k = 3, double minScore = 0.5)
    {
        var found = new List<(double Score, Dictionary<string, object?> Row)>();
        foreach (var past in All(Fingerprint(inc)))
        {
            var (score, parts) = Similarity.Compare(inc, past);
            if (score < minScore) continue;
            found.Add((score, new()
            {
                ["id"] = past.Id, ["title"] = past.Title, ["score"] = score, ["components"] = parts,
                ["onset"] = past.Onset.ToString("yyyy-MM-ddTHH:mm:sszzz"), ["severity"] = past.Severity,
                ["recurring"] = score >= Similarity.RecurrenceScore, ["data_fingerprint"] = Fingerprint(past),
            }));
        }
        return found.OrderByDescending(f => f.Score).Take(k).Select(f => f.Row).ToList();
    }
}
