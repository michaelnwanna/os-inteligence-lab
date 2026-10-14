namespace Ail.Schemas;

public static class Severity
{
    public static readonly string[] Levels = { "low", "moderate", "high", "critical" };
    public static int Order(string s) => Array.IndexOf(Levels, s);
    public static string Max(IEnumerable<string> levels) => levels.OrderByDescending(Order).First();
}

/// <summary>Standard output of every detector.</summary>
public sealed class AnomalyResult
{
    public string Metric { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public DateTimeOffset? Onset { get; set; }
    public double Baseline { get; set; }
    public double ObservedValue { get; set; }
    public double Delta { get; set; }
    public string Severity { get; set; } = "low";
    public double SeverityScore { get; set; }
    public double Confidence { get; set; }
    public double AffectedPopulation { get; set; }
    public string Detector { get; set; } = "";
    public Dictionary<string, string> Dimensions { get; set; } = new();
    public Dictionary<string, object?> Evidence { get; set; } = new();
}

public sealed class CohortFinding
{
    public string Metric { get; set; } = "";
    public Dictionary<string, string> Dimensions { get; set; } = new();
    public double Sessions { get; set; }
    public double Baseline { get; set; }
    public double Observed { get; set; }
    public double Ratio { get; set; }
    public double Delta { get; set; }
    public double Llr { get; set; }
    public double PValue { get; set; }
    public string Severity { get; set; } = "low";
    public double PopulationShare { get; set; }
    public double ExcessShare { get; set; }
    public int Rank { get; set; }
}

public sealed class CorrelationCandidate
{
    public ChangeEvent Change { get; set; } = new();
    public double Score { get; set; }
    public double TemporalScore { get; set; }
    public double Overlap { get; set; }
    public double DeltaMinutes { get; set; }
    public bool AfterOnset { get; set; }
    public string Rationale { get; set; } = "";
}

public sealed class TimelineEvent
{
    public DateTimeOffset Ts { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool SimulatedLatency { get; set; }
    public Dictionary<string, object?> Evidence { get; set; } = new();
}

public sealed class EvidenceItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Statement { get; set; } = "";
    public Dictionary<string, object?> Data { get; set; } = new();
}

public sealed class Hypothesis
{
    public string Title { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string Confidence { get; set; } = "low";
    public List<string> SupportingEvidence { get; set; } = new();
    public string Label { get; set; } = "hypothesis";
}

public sealed class RecommendedStep
{
    public string Step { get; set; } = "";
    public List<string> Evidence { get; set; } = new();
}

public sealed class CandidateCommit
{
    public string Sha { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public string? Component { get; set; }
    public double Score { get; set; }
    public int Rank { get; set; }
    public List<string> Reasons { get; set; } = new();
    public List<string> Snippets { get; set; } = new();
}

public sealed class InvestigationResult
{
    public string Provider { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<EvidenceItem> ObservedFacts { get; set; } = new();
    public List<Hypothesis> Hypotheses { get; set; } = new();
    public List<RecommendedStep> RecommendedSteps { get; set; } = new();
    public List<CandidateCommit> CandidateCommits { get; set; } = new();
    public Dictionary<string, object?> Guardrail { get; set; } = new();
}

public sealed class Incident
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "open";
    public string Severity { get; set; } = "low";
    public string Metric { get; set; } = "";
    public DateTimeOffset Onset { get; set; }
    public DateTimeOffset FirstDetection { get; set; }
    public DateTimeOffset? Confirmed { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public double Baseline { get; set; }
    public double Observed { get; set; }
    public double GlobalBaseline { get; set; }
    public double GlobalObserved { get; set; }
    public List<Dictionary<string, object?>> SupportingMetrics { get; set; } = new();
    public List<AnomalyResult> Results { get; set; } = new();
    public List<CohortFinding> Cohorts { get; set; } = new();
    public List<Dictionary<string, object?>> Signatures { get; set; } = new();
    public List<CorrelationCandidate> Correlations { get; set; } = new();
    public List<Dictionary<string, object?>> SimilarIncidents { get; set; } = new();
    public InvestigationResult? Investigation { get; set; }
    public List<TimelineEvent> Timeline { get; set; } = new();
    public Dictionary<string, object?>? Timemachine { get; set; }
    public Dictionary<string, object?> Reproducibility { get; set; } = new();
}
