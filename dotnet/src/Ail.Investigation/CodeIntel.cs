using System.Text.RegularExpressions;
using Ail.Schemas;

namespace Ail.Investigation;

/// <summary>
/// Heuristic code intelligence: rank commits in a release range as candidate causes. This is review triage, not proof.
/// Signals: the failure-signature component in changed files, symptom keywords, risky patterns added (or mitigations
/// removed) in the diff, architecture-specific changes and the commit message.
/// </summary>
public static class CodeIntel
{
    private static readonly Dictionary<string, string[]> SymptomKeywords = new()
    {
        ["memory"] = new[] { "cache", "buffer", "pool", "alloc", "retain", "static", "singleton", "shared", "heap", "lifetime" },
        ["latency"] = new[] { "sync", "lock", "mutex", "query", "retry", "sleep", "loop", "wait", "blocking" },
        ["failure"] = new[] { "exception", "throw", "null", "abort", "assert", "crash", "timeout" },
    };

    private static readonly Regex[] Risk = new[]
    {
        @"\bstatic\b.*\b(map|vector|list|set)\b", @"make_shared|shared_ptr", @"\bentries_?\[", @"emplace|push_back", @"\bcache\b",
        @"\bretain", @"keep\s+\w*\s*alive", @"strong\s+ref",
    }.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();

    private static readonly Regex[] Mitigation = new[]
    {
        @"\berase\b", @"\bevict", @"\bremove\b", @"\bclear\b", @"\brelease\b", @"weak_ptr|WeakReference", @"dispose|\bfree\(|\bdelete\b",
    }.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();

    private static readonly Dictionary<string, string[]> ArchTokens = new()
    {
        ["arm64"] = new[] { "arm", "aarch64", "neon" }, ["x64"] = new[] { "x64", "x86_64", "avx", "sse" },
    };

    private static string Norm(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]", "");

    private static List<string> Symptoms(string metric, IReadOnlyList<string> signatures, IReadOnlyList<string> supportingSignificant)
    {
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        var blob = string.Join(" ", signatures).ToLowerInvariant();
        if (metric == "memory_mb" || supportingSignificant.Contains("memory_mb") || blob.Contains("oom") || blob.Contains("memory")) kinds.Add("memory");
        if (metric == "latency_ms" || blob.Contains("timeout")) kinds.Add("latency");
        if (metric == "failure_rate") kinds.Add("failure");
        return kinds.SelectMany(k => SymptomKeywords[k]).ToList();
    }

    private static List<string> SignatureComponents(IEnumerable<string> signatures) =>
        signatures.SelectMany(s => s.Split('|').Last().Split('.')).Where(p => p.Length > 3).Distinct().ToList();

    public static List<CandidateCommit> RankCommits(IReadOnlyList<CommitInfo> commits, string metric, IReadOnlyList<string> signatures,
        IReadOnlyList<string> supportingSignificant, string? architecture, int top = 5)
    {
        var keywords = Symptoms(metric, signatures, supportingSignificant);
        var components = SignatureComponents(signatures);
        var archTokens = ArchTokens.GetValueOrDefault((architecture ?? "").ToLowerInvariant(), Array.Empty<string>());
        var scored = new List<(double Score, CommitInfo Commit, List<string> Reasons, List<string> Snippets, string? Matched)>();

        foreach (var c in commits)
        {
            var reasons = new List<string>(); var snippets = new List<string>();
            var pathsNorm = string.Join(" ", c.Files.Select(f => Norm(f.Path)));
            var lines = c.Files.SelectMany(f => f.Patch.Split('\n')).ToList();
            var added = lines.Where(l => l.StartsWith('+')).Select(l => l[1..].Trim()).ToList();
            var removed = lines.Where(l => l.StartsWith('-')).Select(l => l[1..].Trim()).ToList();
            var addedBlob = string.Join(" ", added).ToLowerInvariant();

            var matched = components.FirstOrDefault(comp => pathsNorm.Contains(Norm(comp)));
            var sigScore = matched is not null ? 1.0 : 0.0;
            if (matched is not null) reasons.Add($"changes files for failure-signature component '{matched}'");

            var hits = keywords.Where(kw => pathsNorm.Contains(kw) || addedBlob.Contains(kw)).Distinct().ToList();
            var kwScore = Math.Min(1.0, hits.Count / 3.0);
            if (hits.Count > 0) reasons.Add("symptom keywords in changed code: " + string.Join(", ", hits.OrderBy(x => x, StringComparer.Ordinal).Take(4)));

            var riskLines = added.Where(l => Risk.Any(r => r.IsMatch(l))).ToList();
            var mitAdded = added.Count(l => Mitigation.Any(r => r.IsMatch(l)));
            var mitRemoved = removed.Where(l => Mitigation.Any(r => r.IsMatch(l))).ToList();
            var netRisk = riskLines.Count + 2 * mitRemoved.Count - mitAdded;
            var riskScore = Math.Clamp(netRisk / 4.0, 0.0, 1.0);
            if (riskLines.Count > 0)
            {
                reasons.Add($"adds {riskLines.Count} risky retention/allocation pattern(s) without matching cleanup");
                snippets.AddRange(riskLines.Take(2));
            }
            if (mitRemoved.Count > 0)
            {
                reasons.Add($"removes {mitRemoved.Count} cleanup/release line(s)");
                snippets.Add(mitRemoved[0]);
            }

            var archHit = archTokens.Length > 0 && archTokens.Any(t => pathsNorm.Contains(t) || addedBlob.Contains(t));
            if (archHit) reasons.Add($"touches {architecture} specific code");
            var msgHit = keywords.Any(kw => c.Message.ToLowerInvariant().Contains(kw));

            var score = 0.35 * sigScore + 0.25 * kwScore + 0.25 * riskScore + 0.10 * (archHit ? 1 : 0) + 0.05 * (msgHit ? 1 : 0);
            scored.Add((Math.Round(score, 4), c, reasons, snippets, matched));
        }

        return scored.OrderByDescending(s => s.Score).Take(top).Select((s, i) =>
        {
            var stem = s.Commit.Files.Count > 0 ? Path.GetFileNameWithoutExtension(s.Commit.Files[0].Path) : "";
            var camel = string.Concat(Regex.Split(stem, "[_-]").Select(p => p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
            return new CandidateCommit
            {
                Sha = s.Commit.Sha, Message = s.Commit.Message, Files = s.Commit.Files.Select(f => f.Path).ToList(),
                Component = s.Matched ?? (camel.Length > 0 ? camel : null), Score = s.Score, Rank = i + 1, Reasons = s.Reasons, Snippets = s.Snippets,
            };
        }).ToList();
    }
}
