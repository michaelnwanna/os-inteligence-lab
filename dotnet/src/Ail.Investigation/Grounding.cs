using System.Text.Json;
using System.Text.RegularExpressions;
using Ail.Schemas;

namespace Ail.Investigation;

/// <summary>Guardrails: model output may explain the evidence but must not invent telemetry facts.</summary>
public static class Grounding
{
    private static readonly Regex Num = new(@"(?<![\w.])\d+(?:\.\d+)?(?![\w])", RegexOptions.Compiled);
    private static readonly Regex Hex = new(@"\b(?=[0-9a-f]*\d)(?=[0-9a-f]*[a-f])[0-9a-f]{7,40}\b", RegexOptions.Compiled);
    private static readonly Regex Certain = new(@"\b(proves?|proven|definitely|certainly|undoubtedly|guaranteed)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Id = new(@"^[ECH]\d+$", RegexOptions.Compiled);

    public static HashSet<double> NumbersIn(string text) =>
        Num.Matches(text).Select(m => Math.Round(double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture), 6)).ToHashSet();

    /// <summary>Returns violations; an empty list means the output is acceptable.</summary>
    public static List<string> Validate(JsonElement raw, IReadOnlyList<EvidenceItem> evidence, IReadOnlyList<string> knownShas)
    {
        var problems = new List<string>();
        string? Str(JsonElement e, string name, int min, int max, string where)
        {
            if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) { problems.Add($"schema: {where}.{name} must be a string"); return null; }
            var s = p.GetString()!;
            if (s.Length < min || s.Length > max) problems.Add($"schema: {where}.{name} has invalid length");
            return s;
        }
        List<string> Refs(JsonElement e, string name, string where)
        {
            var list = new List<string>();
            if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) { problems.Add($"schema: {where}.{name} must be a list"); return list; }
            foreach (var x in p.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString()!);
            return list;
        }

        if (raw.ValueKind != JsonValueKind.Object) return new() { "schema: output must be an object" };
        var summary = Str(raw, "summary", 3, 3000, "output");
        if (!raw.TryGetProperty("hypotheses", out var hyps) || hyps.ValueKind != JsonValueKind.Array || hyps.GetArrayLength() is < 1 or > 5)
            problems.Add("schema: hypotheses must be a list of 1 to 5 items");
        if (!raw.TryGetProperty("recommended_steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() > 8)
            problems.Add("schema: recommended_steps must be a list of at most 8 items");
        if (problems.Count > 0) return problems;

        var ids = evidence.Select(e => e.Id).ToHashSet();
        var allowed = evidence.SelectMany(e => NumbersIn(e.Statement)).ToHashSet();

        void CheckText(string where, string text)
        {
            var extra = NumbersIn(text).Except(allowed).OrderBy(x => x).ToList();
            if (extra.Count > 0) problems.Add($"{where}: numbers not present in evidence: [{string.Join(", ", extra)}]");
            foreach (Match m in Hex.Matches(text))
                if (!knownShas.Any(sha => sha.StartsWith(m.Value))) problems.Add($"{where}: unknown commit hash '{m.Value}'");
            if (Certain.IsMatch(text)) problems.Add($"{where}: asserts certainty about a hypothesis");
        }

        void CheckRefs(string where, List<string> refs)
        {
            foreach (var r in refs) if (!Id.IsMatch(r) || !ids.Contains(r)) problems.Add($"{where}: cites unknown evidence '{r}'");
        }

        CheckText("summary", summary!);
        var i = 0;
        foreach (var h in hyps.EnumerateArray())
        {
            var where = $"hypothesis[{i++}]";
            var title = Str(h, "title", 3, 300, where); var rationale = Str(h, "rationale", 3, 2000, where);
            var confidence = Str(h, "confidence", 1, 20, where);
            var refs = Refs(h, "supporting_evidence", where);
            if (problems.Any(p => p.StartsWith("schema:"))) return problems;
            CheckText(where, $"{title} {rationale}");
            if (confidence is not ("low" or "medium" or "high")) problems.Add($"{where}: invalid confidence '{confidence}'");
            if (refs.Count == 0) problems.Add($"{where}: no supporting evidence cited");
            CheckRefs(where, refs);
        }
        i = 0;
        foreach (var s in steps.EnumerateArray())
        {
            var where = $"step[{i++}]";
            var text = Str(s, "step", 3, 600, where);
            var refs = s.TryGetProperty("evidence", out _) ? Refs(s, "evidence", where) : new List<string>();
            if (problems.Any(p => p.StartsWith("schema:"))) return problems;
            CheckText(where, text!);
            CheckRefs(where, refs);
        }
        return problems;
    }
}
