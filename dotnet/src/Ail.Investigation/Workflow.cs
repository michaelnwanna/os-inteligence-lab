using Ail.Detection;
using Ail.Ingestion;
using Ail.Schemas;

namespace Ail.Investigation;

public static class Workflow
{
    /// <summary>Full analysis: detect, investigate, build the replay, persist.</summary>
    public static List<Incident> RunAnalysis(TelemetryStore store, DetectionConfig? cfg = null, ILlmProvider? llm = null,
        string repoMode = "auto", bool investigate = true, bool save = true)
    {
        cfg ??= new DetectionConfig();
        var ws = new Workspace(store, cfg);
        var incidents = IncidentAnalyzer.Analyze(store, cfg, ws: ws);
        var agent = new InvestigationAgent(llm, repoMode);
        var windows = new Dictionary<string, Workspace>();
        foreach (var inc in incidents)
        {
            var start = (string)inc.Reproducibility["analysis_start"]!; // later episodes were analysed on a re-baselined window
            if (!windows.TryGetValue(start, out var window))
                windows[start] = window = new Workspace(store, cfg, TimeUtil.BucketOf(DateTimeOffset.Parse(start)));
            if (investigate)
            {
                inc.Investigation = agent.Investigate(inc);
                if (inc.Status == "open") inc.Status = "investigating";
            }
            var (events, data) = TimeMachine.Build(window, inc);
            inc.Timeline = events;
            inc.Timemachine = data;
        }
        if (save) store.ReplaceIncidents(incidents);
        return incidents;
    }
}
