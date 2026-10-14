using System.Text.RegularExpressions;
using Ail.Schemas;

namespace Ail.Detection;

/// <summary>
/// Natural-language telemetry questions, answered by the same statistics as the rest of the platform. The parser is rule
/// based and small: it extracts a metric, a time window and include/exclude cohorts. The parsed query is returned with
/// every answer so the user can see how the question was understood.
/// </summary>
public sealed class NlqQuery
{
    public string Text { get; init; } = "";
    public string Metric { get; set; } = "failure_rate";
    public Dictionary<string, string> Include { get; set; } = new();
    public List<Dictionary<string, string>> Exclude { get; } = new();
    public int? WindowMinutes { get; set; }
    public List<string> Notes { get; } = new();
}

public static class Nlq
{
    private static readonly Dictionary<string, int> NumberWords = new()
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["twelve"] = 12, ["twenty"] = 20, ["thirty"] = 30,
    };
    private static readonly Dictionary<string, int> UnitMinutes = new() { ["minute"] = 1, ["min"] = 1, ["hour"] = 60, ["day"] = 1440, ["week"] = 10080 };
    private static readonly Regex ExcludeSplit = new(@"\b(?:but not|excluding|except(?: for)?|without|not on|and not)\b", RegexOptions.Compiled);
    private static readonly Regex Window = new(@"(?:last|past|previous|within the last)\s+(\d+|[a-z]+)\s+(minute|min|hour|day|week)s?", RegexOptions.Compiled);
    private const string Version = @"(\d+(?:\.\d+)*)";
    private static readonly (string Metric, string[] Words)[] MetricWords =
    {
        ("latency_ms", new[] { "latency", "slow", "slower", "response time" }),
        ("memory_mb", new[] { "memory", "leak", "ram" }),
    };

    private static Dictionary<string, string> CohortTerms(string text, IReadOnlyDictionary<string, List<string>> vocab)
    {
        var found = new Dictionary<string, string>();
        foreach (var arch in vocab.GetValueOrDefault("architecture") ?? new())
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(arch.ToLowerInvariant())}\b")) found["architecture"] = arch;
        foreach (var model in vocab.GetValueOrDefault("device_model") ?? new())
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(model.ToLowerInvariant())}\b")) found["device_model"] = model;
        foreach (var (key, pattern) in new[]
        {
            ("application_version", $@"\b(?:app(?:lication)?|version|v)\s*{Version}"),
            ("driver_version", $@"\bdriver\s*{Version}"),
            ("os_version", $@"\b(?:os|build)\s*{Version}"),
        })
        {
            var m = Regex.Match(text, pattern);
            if (m.Success && (vocab.GetValueOrDefault(key) ?? new()).Contains(m.Groups[1].Value)) found[key] = m.Groups[1].Value;
        }
        return found;
    }

    public static NlqQuery Parse(string text, IReadOnlyDictionary<string, List<string>> vocab)
    {
        var low = text.ToLowerInvariant().Trim().TrimEnd('?', '.');
        var q = new NlqQuery { Text = text };
        foreach (var (metric, words) in MetricWords)
            if (words.Any(low.Contains)) { q.Metric = metric; break; }
        var m = Window.Match(low);
        if (m.Success)
        {
            var raw = m.Groups[1].Value;
            int? n = int.TryParse(raw, out var parsed) ? parsed : NumberWords.TryGetValue(raw, out var w) ? w : null;
            if (n is > 0) q.WindowMinutes = n * UnitMinutes[m.Groups[2].Value];
            else q.Notes.Add($"could not read the time window '{raw}'");
        }
        var split = ExcludeSplit.Match(low);
        var left = split.Success ? low[..split.Index] : low;
        q.Include = CohortTerms(left, vocab);
        if (split.Success)
        {
            var excluded = CohortTerms(low[(split.Index + split.Length)..], vocab);
            if (excluded.Count > 0) q.Exclude.Add(excluded);
            else q.Notes.Add("could not recognise the excluded cohort");
        }
        if (q.Include.Count == 0) q.Notes.Add("no cohort recognised; showing the whole fleet");
        return q;
    }

    private static Dictionary<string, object?> Verdict(CohortEval ev, string kind, Workspace ws)
    {
        var minRatio = kind == "rate" ? ws.Cfg.Cohort.MinRatioRate : ws.Cfg.Cohort.MinRatioGauge;
        return new()
        {
            ["sessions"] = ev.Sessions, ["observed"] = ev.Observed, ["baseline"] = ev.Baseline, ["ratio"] = ev.Ratio, ["llr"] = ev.Llr,
            ["regressed"] = ev.Llr > 12 && ev.Ratio >= minRatio,
        };
    }

    public static Dictionary<string, object?> Answer(Workspace ws, NlqQuery q, IEnumerable<Incident>? incidents = null)
    {
        var cube = ws.Cube(q.Metric); var scanner = ws.Scanner(q.Metric);
        var end = cube.T;
        var start = q.WindowMinutes is { } wm ? Math.Max(ws.BaselineEnd, end - wm) : ws.BaselineEnd;
        var clipped = q.WindowMinutes is { } w2 && end - w2 < ws.BaselineEnd;
        var incStats = Verdict(scanner.Evaluate(cube.Mask(q.Include), start, end), cube.Kind, ws);
        var excl = new List<Dictionary<string, object?>>();
        foreach (var dims in q.Exclude)
        {
            var stats = Verdict(scanner.Evaluate(cube.Mask(dims), start, end), cube.Kind, ws);
            excl.Add(new Dictionary<string, object?> { ["cohort"] = Labels.CohortLabel(dims), ["dimensions"] = dims }.Concat(stats).ToDictionary(kv => kv.Key, kv => kv.Value));
        }
        var matches = new List<Dictionary<string, object?>>();
        foreach (var inc in incidents ?? Array.Empty<Incident>())
        {
            var dims = inc.Cohorts.FirstOrDefault()?.Dimensions ?? new Dictionary<string, string>();
            if (inc.Metric != q.Metric || q.Include.Any(kv => dims.GetValueOrDefault(kv.Key) != kv.Value)) continue;
            if (q.Exclude.Any(ex => ex.All(kv => dims.GetValueOrDefault(kv.Key) == kv.Value))) continue;
            if (inc.Onset.ToUnixTimeSeconds() / 60.0 < ws.T0 + start) continue;
            matches.Add(new() { ["id"] = inc.Id, ["title"] = inc.Title, ["severity"] = inc.Severity, ["cohort"] = Labels.CohortLabel(dims), ["onset"] = inc.Onset.ToString("yyyy-MM-ddTHH:mm:sszzz") });
        }

        var okExcl = excl.All(e => e["regressed"] is not true);
        var scope = Labels.CohortLabel(q.Include);
        var unit = q.Metric;
        double R(Dictionary<string, object?> d, string k) => (double)d[k]!;
        string verdict, text;
        if (incStats["regressed"] is true && okExcl)
        {
            verdict = "regression";
            text = $"Yes. {Labels.MetricLabel(unit)} for {scope} is {Labels.FormatValue(unit, R(incStats, "observed"))} against an expected " +
                   $"{Labels.FormatValue(unit, R(incStats, "baseline"))} ({R(incStats, "ratio"):F1}x)" +
                   string.Concat(excl.Select(e => $", while {e["cohort"]} is not elevated ({R(e, "ratio"):F1}x)")) + ".";
        }
        else if (incStats["regressed"] is true)
        {
            verdict = "also_affects_excluded";
            text = $"{scope} is elevated ({R(incStats, "ratio"):F1}x) but so is " + string.Join(", ", excl.Where(e => e["regressed"] is true).Select(e => e["cohort"])) +
                   ", so this is not specific to the first cohort.";
        }
        else
        {
            verdict = "no_regression";
            text = $"No significant {Labels.MetricLabel(unit).ToLowerInvariant()} regression for {scope} ({R(incStats, "ratio"):F1}x expected).";
        }
        return new()
        {
            ["query"] = new Dictionary<string, object?>
            {
                ["text"] = q.Text, ["metric"] = q.Metric, ["include"] = q.Include, ["exclude"] = q.Exclude,
                ["window_minutes"] = q.WindowMinutes, ["notes"] = q.Notes,
            },
            ["window"] = new[] { TimeUtil.Hhmm(ws.T0 + start), TimeUtil.Hhmm(ws.T0 + end - 1) },
            ["window_clipped_to_available_data"] = clipped || (q.WindowMinutes ?? 0) > end,
            ["verdict"] = verdict, ["answer"] = text,
            ["include"] = new Dictionary<string, object?> { ["cohort"] = scope }.Concat(incStats).ToDictionary(kv => kv.Key, kv => kv.Value),
            ["exclude"] = excl, ["incidents"] = matches,
        };
    }

    public static Dictionary<string, List<string>> VocabOf(Workspace ws)
    {
        var cube = ws.Cube("failure_rate");
        return Dimensions.All.ToDictionary(d => d, d => cube.Vocab[d].Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList());
    }
}
