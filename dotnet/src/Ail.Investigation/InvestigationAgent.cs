using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ail.Ingestion;
using Ail.Schemas;

namespace Ail.Investigation;

/// <summary>The investigation agent: evidence in, grounded hypotheses out.</summary>
public sealed class InvestigationAgent
{
    private readonly ILlmProvider _llm;
    private readonly string _repoMode;
    private readonly IRepoProvider? _repo;

    public InvestigationAgent(ILlmProvider? llm = null, string repoMode = "auto", IRepoProvider? repo = null)
    {
        _llm = llm ?? new RulesProvider(); _repoMode = repoMode; _repo = repo;
    }

    private static JsonElement ExtractJson(string text)
    {
        text = text.Trim();
        var fence = Regex.Match(text, @"^```(?:json)?\s*(.*?)\s*```$", RegexOptions.Singleline);
        using var doc = JsonDocument.Parse(fence.Success ? fence.Groups[1].Value : text);
        return doc.RootElement.Clone();
    }

    private (List<CandidateCommit> Candidates, int Total, string? RepoName, (string, string)? Refs) CodeCandidates(Incident inc)
    {
        var lead = inc.Correlations.FirstOrDefault(c => !c.AfterOnset && c.Score >= 0.3 && c.Change.Repository is not null && c.Change.BaseRef is not null && c.Change.HeadRef is not null);
        if (lead is null) return (new(), 0, null, null);
        var ch = lead.Change;
        var repo = _repo ?? RepoProviders.Get(ch.Repository, _repoMode);
        if (repo is null) return (new(), 0, null, null);
        List<CommitInfo> commits;
        try { commits = repo.Compare(ch.BaseRef!, ch.HeadRef!); }
        catch (Exception) { return (new(), 0, null, null); }
        var signatures = inc.Signatures.Where(s => (double)s["excess_share"]! >= 0.2).Select(s => (string)s["signature"]!).ToList();
        var significant = inc.SupportingMetrics.Where(s => s["significant"] is true).Select(s => (string)s["metric"]!).ToList();
        var arch = inc.Cohorts.FirstOrDefault()?.Dimensions.GetValueOrDefault("architecture");
        return (CodeIntel.RankCommits(commits, inc.Metric, signatures, significant, arch), commits.Count, repo.Name, (ch.BaseRef!, ch.HeadRef!));
    }

    public InvestigationResult Investigate(Incident inc)
    {
        var start = Stopwatch.GetTimestamp();
        var reg = MetricsRegistry.Instance;
        var (candidates, total, repoName, refs) = CodeCandidates(inc);
        var evidence = EvidenceBuilder.Build(inc, candidates, total, repoName, refs);
        var knownShas = candidates.Select(c => c.Sha).ToList();
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["incident"] = new { id = inc.Id, title = inc.Title },
            ["evidence"] = evidence.Select(e => new Dictionary<string, object?>
            {
                ["id"] = e.Id, ["kind"] = e.Kind, ["statement"] = e.Statement,
                ["data"] = e.Data.Where(kv => kv.Key != "message").ToDictionary(kv => kv.Key, kv => kv.Value),
            }).ToList(),
        }, AilJson.Options);

        var guardrail = new Dictionary<string, object?>
        {
            ["provider"] = _llm.Name, ["evidence_items"] = evidence.Count, ["facts_source"] = "deterministic statistics (never model-generated)",
        };
        JsonElement? output = null;
        var used = _llm.Name;
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            var result = _llm.Complete(Prompts.System, payload);
            reg.Observe("ail_ai_request_latency_seconds", Stopwatch.GetElapsedTime(t0).TotalSeconds);
            reg.Inc("ail_ai_tokens_total", result.PromptTokens + result.CompletionTokens);
            var raw = ExtractJson(result.Text);
            var violations = Grounding.Validate(raw, evidence, knownShas);
            guardrail["provider_output_valid"] = violations.Count == 0;
            guardrail["violations"] = violations.Take(10).ToList();
            if (violations.Count == 0) output = raw;
        }
        catch (Exception ex)
        {
            reg.Inc("ail_ai_errors_total");
            guardrail["provider_output_valid"] = false;
            guardrail["violations"] = new List<string> { $"provider error: {ex.GetType().Name}" };
        }

        if (output is null)
        {
            var fallback = new RulesProvider();
            var raw = ExtractJson(fallback.Complete(Prompts.System, payload).Text);
            var violations = Grounding.Validate(raw, evidence, knownShas);
            if (violations.Count > 0) throw new InvalidOperationException($"fallback output failed grounding: {string.Join("; ", violations)}");
            output = raw;
            used = fallback.Name;
            guardrail["fallback_used"] = _llm.Name != fallback.Name;
        }
        guardrail["generated_by"] = used;
        reg.Observe("ail_investigation_duration_seconds", Stopwatch.GetElapsedTime(start).TotalSeconds);

        var o = output.Value;
        return new InvestigationResult
        {
            Provider = used, Summary = o.GetProperty("summary").GetString()!, ObservedFacts = evidence,
            Hypotheses = o.GetProperty("hypotheses").EnumerateArray().Select(h => new Hypothesis
            {
                Title = h.GetProperty("title").GetString()!, Rationale = h.GetProperty("rationale").GetString()!,
                Confidence = h.GetProperty("confidence").GetString()!,
                SupportingEvidence = h.GetProperty("supporting_evidence").EnumerateArray().Select(x => x.GetString()!).ToList(),
            }).ToList(),
            RecommendedSteps = o.GetProperty("recommended_steps").EnumerateArray().Select(s => new RecommendedStep
            {
                Step = s.GetProperty("step").GetString()!,
                Evidence = s.TryGetProperty("evidence", out var e) ? e.EnumerateArray().Select(x => x.GetString()!).ToList() : new(),
            }).ToList(),
            CandidateCommits = candidates, Guardrail = guardrail,
        };
    }
}
